using System.Diagnostics;
using MediaRack.Files;
using MediaRack.Media;
using MediaRack.Models;

namespace MediaRack.Scan;

/// <summary>
/// 单流源扫描器：任意层级的普通视频文件。
/// <para>
/// 布局约定：<b>物理目录即合集</b>——
/// 每个子目录一个合集，根目录里的散落文件归"根目录"合集（见 docs/design.md §3.4）。
/// </para>
/// <para>
/// 与 dual 源的关键差别：单流文件<b>没有完整性契约</b>（没有 md5 清单可对照），
/// 因此不存在 <c>corrupt</c> 判定，只区分"存在/0 字节/读不出元数据"。
/// </para>
/// </summary>
public sealed class SingleScanner(IMediaProbe? probe = null)
{
    /// <param name="progress">
    /// 逐条上报进度。先把候选文件枚举成列表以便给出总数：走一遍目录树只是列目录，
    /// 和后面每条一次 ffprobe 相比可以忽略。
    /// </param>
    public ScanResult Scan(
        SourceConfig source,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Layout != SourceLayout.Single)
        {
            throw new ArgumentException($"源 {source.Id} 不是 single 布局", nameof(source));
        }

        var sw = Stopwatch.StartNew();

        // single 源没有 md5 契约，verify 无意义；报告里固定标 None，免得读出"校验过"的错觉。
        var report = new ScanReport { SourceId = source.Id, Verify = VerifyMode.None };
        var entries = new List<MediaEntry>();

        // 源根在整次扫描里不变，取一次非空值，省得每个调用点都写 !
        var root = source.Root!;
        var files = FileWalker.EnumerateVideos(root, ct).ToList();

        for (var index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var file = files[index];
            var relPath = NormalizeRelative(Path.GetRelativePath(root, file));

            progress?.Report(new ScanProgress(ScanStage.Scan, index + 1, files.Count, relPath));

            try
            {
                var scanned = ScanOne(source, file, relPath);
                entries.Add(scanned.Entry);
                report.Record(scanned.Entry, scanned.Problem);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不到不该中断整次扫描。
                report.Problems.Add(new ScanProblem(relPath, ex.Message, Fatal: true));
            }
        }

        sw.Stop();
        report.Elapsed = sw.Elapsed;
        return new ScanResult(report, entries);
    }

    private ScannedFile ScanOne(SourceConfig source, string file, string relPath)
    {
        var relDir = Path.GetDirectoryName(relPath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(relDir)) relDir = null;

        var info = new FileInfo(file);
        var size = info.Length;
        var key = MediaKey.ForSingle(relPath);

        var entry = new MediaEntry
        {
            SourceId = source.Id,
            Key = key,
            RelPath = relPath,
            RelDir = relDir,
            Layout = SourceLayout.Single,
            TitleRaw = Path.GetFileNameWithoutExtension(file),
            SizeBytes = size,
            // 下次扫描判变用：mtime + size 都没变就跳过 ffprobe 沿用库值。
            FileMtime = info.Exists
                ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : null,
            Status = MediaStatus.Ok,
            ScannedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Tracks =
            [
                new MediaTrack
                {
                    Role = TrackRole.Video,
                    SizeBytes = size,
                },
            ],
        };

        if (size == 0)
        {
            entry.Status = MediaStatus.Corrupt;
            return new ScannedFile(entry, new ScanProblem(relPath, "文件为 0 字节"));
        }

        if (probe is null) return new ScannedFile(entry, null);

        if (!probe.TryProbe(file, out var probed, out var error))
        {
            // 读不出元数据仍入库：文件在、能播，只是缺时长/分辨率与缩略图。
            return new ScannedFile(entry, new ScanProblem(relPath, $"无法解析媒体信息：{error}"));
        }

        entry.DurationMs = probed.DurationMs;
        entry.Width = probed.Width;
        entry.Height = probed.Height;
        entry.Codec = probed.Codec;

        return new ScannedFile(entry, null);
    }

    /// <summary>统一成 <c>/</c> 分隔且不以 <c>./</c> 开头；键与库里的路径必须完全一致。</summary>
    private static string NormalizeRelative(string relPath)
    {
        var s = relPath.Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        return s;
    }

    private sealed record ScannedFile(MediaEntry Entry, ScanProblem? Problem);
}
