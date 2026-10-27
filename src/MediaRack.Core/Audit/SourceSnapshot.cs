using System.Security.Cryptography;
using MediaRack.Files;

namespace MediaRack.Audit;

/// <summary>指纹强度。</summary>
public enum FingerprintMode
{
    /// <summary>路径 + 字节数 + 修改时间。快，覆盖"被改写/被替换/被增删"的全部常见情形。</summary>
    Metadata,

    /// <summary>追加全文件 md5。慢，用于拿不出任何辩解的强证据。</summary>
    Md5,
}

/// <summary>一个文件的指纹。路径是相对源根的 POSIX 形式，换盘不失效。</summary>
public sealed record FileFingerprint(string RelPath, long Size, long MTimeTicks, string? Hash);

/// <summary>
/// 源目录的一次全量快照。
/// <para>
/// 只读取，不写入。跳过 OS 垃圾文件（<c>Thumbs.db</c> / <c>desktop.ini</c> / <c>._*</c>）——
/// 它们不是内容，且会被系统与同步工具随时改写，纳入比对只会产生噪声。
/// </para>
/// </summary>
public sealed class SourceSnapshot
{
    private SourceSnapshot(
        string root, FingerprintMode mode,
        IReadOnlyList<FileFingerprint> files, IReadOnlyList<string> directories)
    {
        Root = root;
        Mode = mode;
        Files = files;
        Directories = directories;
    }

    public string Root { get; }
    public FingerprintMode Mode { get; }

    /// <summary>按相对路径升序排列。</summary>
    public IReadOnlyList<FileFingerprint> Files { get; }

    /// <summary>相对路径升序排列；不含源根本身。空目录也算——新建目录同样是写操作。</summary>
    public IReadOnlyList<string> Directories { get; }

    public long TotalBytes => Files.Sum(f => f.Size);

    public static SourceSnapshot Take(
        string root, FingerprintMode mode = FingerprintMode.Metadata, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(root);
        var files = new List<FileFingerprint>();
        var dirs = new List<string>();

        foreach (var path in FileWalker.Enumerate(full, _ => true, ct))
        {
            var info = new FileInfo(path);
            if (!info.Exists) continue;

            files.Add(new FileFingerprint(
                RelPath: ToRelative(full, path),
                Size: info.Length,
                MTimeTicks: info.LastWriteTimeUtc.Ticks,
                Hash: mode == FingerprintMode.Md5 ? Hash(path) : null));
        }

        CollectDirectories(full, dirs, ct);

        files.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
        dirs.Sort(StringComparer.Ordinal);

        return new SourceSnapshot(full, mode, files, dirs);
    }

    /// <summary>
    /// 多文件夹源：每个文件夹单独快照后合并，相对路径加 <c>f{i}/</c> 前缀——
    /// 不同文件夹下的同名相对路径不能被当成同一个文件比对。
    /// </summary>
    public static SourceSnapshot Take(
        IReadOnlyList<string> roots, FingerprintMode mode = FingerprintMode.Metadata, CancellationToken ct = default)
    {
        if (roots.Count == 0)
        {
            return new SourceSnapshot(string.Empty, mode, [], []);
        }

        if (roots.Count == 1) return Take(roots[0], mode, ct);

        var files = new List<FileFingerprint>();
        var dirs = new List<string>();
        for (var i = 0; i < roots.Count; i++)
        {
            var one = Take(roots[i], mode, ct);
            files.AddRange(one.Files.Select(f => f with { RelPath = $"f{i}/{f.RelPath}" }));
            dirs.AddRange(one.Directories.Select(d => $"f{i}/{d}"));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
        dirs.Sort(StringComparer.Ordinal);
        return new SourceSnapshot(string.Join(" | ", roots), mode, files, dirs);
    }

    /// <summary>相对路径统一成 <c>/</c> 分隔，避免 Windows / POSIX 差异。</summary>
    public static string ToRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Hash(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexStringLower(MD5.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static void CollectDirectories(string root, List<string> into, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] subs;
            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                // 与 FileWalker 保持同一套跳过规则，否则两处快照会互相打架。
                if (name.Length == 0 || name[0] == '.') continue;
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                into.Add(ToRelative(root, sub));
                stack.Push(sub);
            }
        }
    }
}

/// <summary>两次快照的差异。<b>任何一项非空都意味着源目录被改动过。</b></summary>
public sealed record SnapshotDiff(
    IReadOnlyList<FileFingerprint> Added,
    IReadOnlyList<FileFingerprint> Removed,
    IReadOnlyList<(FileFingerprint Before, FileFingerprint After)> Modified,
    IReadOnlyList<string> AddedDirectories,
    IReadOnlyList<string> RemovedDirectories)
{
    public bool IsEmpty =>
        Added.Count == 0 && Removed.Count == 0 && Modified.Count == 0 &&
        AddedDirectories.Count == 0 && RemovedDirectories.Count == 0;

    public int TotalChanges =>
        Added.Count + Removed.Count + Modified.Count +
        AddedDirectories.Count + RemovedDirectories.Count;

    public static SnapshotDiff Compare(SourceSnapshot before, SourceSnapshot after)
    {
        var oldFiles = before.Files.ToDictionary(f => f.RelPath, StringComparer.Ordinal);
        var newFiles = after.Files.ToDictionary(f => f.RelPath, StringComparer.Ordinal);

        var added = newFiles.Values.Where(f => !oldFiles.ContainsKey(f.RelPath)).ToList();
        var removed = oldFiles.Values.Where(f => !newFiles.ContainsKey(f.RelPath)).ToList();

        var modified = new List<(FileFingerprint, FileFingerprint)>();
        foreach (var (rel, oldFile) in oldFiles)
        {
            if (!newFiles.TryGetValue(rel, out var newFile)) continue;
            if (IsChanged(oldFile, newFile)) modified.Add((oldFile, newFile));
        }

        var oldDirs = new HashSet<string>(before.Directories, StringComparer.Ordinal);
        var newDirs = new HashSet<string>(after.Directories, StringComparer.Ordinal);

        return new SnapshotDiff(
            added, removed, modified,
            newDirs.Except(oldDirs, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            oldDirs.Except(newDirs, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    private static bool IsChanged(FileFingerprint a, FileFingerprint b) =>
        a.Size != b.Size ||
        a.MTimeTicks != b.MTimeTicks ||
        (a.Hash is not null && b.Hash is not null && !string.Equals(a.Hash, b.Hash, StringComparison.Ordinal));
}
