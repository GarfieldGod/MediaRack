namespace MediaRack.Files;

/// <summary>
/// 递归枚举文件。
/// <para>
/// 自己走目录栈而不是 <c>Directory.EnumerateFiles(AllDirectories)</c>，因为需要：
/// 跳过隐藏/系统目录、跳过回收站与 NAS 元数据目录、遇到无权限目录不中断整次扫描。
/// （隐藏+系统属性的目录若不显式处理，会把回收站、缩略图缓存当成内容扫进来。）
/// </para>
/// </summary>
public static class FileWalker
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        "@eaDir",          // 群晖缩略图目录
        "#recycle",        // 群晖回收站
        ".videoview",      // 其它播放器的元数据目录，不是内容
        ".mediarack",      // 本项目的元数据目录（源根本身就是一种库时可能出现）
    };

    /// <summary>递归枚举视频文件（绝对路径）。顺序不保证。</summary>
    public static IEnumerable<string> EnumerateVideos(string root, CancellationToken ct = default) =>
        Enumerate(root, VideoFormats.IsVideo, ct);

    /// <summary>递归枚举满足谓词的文件（绝对路径）。</summary>
    public static IEnumerable<string> Enumerate(
        string root, Func<string, bool> predicate, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) yield break;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var dir = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                files = [];
            }

            foreach (var file in files)
            {
                if (IsJunkFile(file)) continue;
                if (predicate(file)) yield return file;
            }

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
                if (ShouldSkipDirectory(sub)) continue;
                stack.Push(sub);
            }
        }
    }

    /// <summary>隐藏目录（前缀点）、已知垃圾目录、符号链接/联接点（防环）。</summary>
    private static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0) return true;
        if (name[0] == '.') return true;
        if (SkipDirectories.Contains(name)) return true;

        try
        {
            var attrs = new DirectoryInfo(path).Attributes;
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到属性就当普通目录处理。
        }

        return false;
    }

    /// <summary>macOS 资源分叉、Windows 缩略图缓存等不是内容。</summary>
    private static bool IsJunkFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("._", StringComparison.Ordinal) ||
               name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }
}
