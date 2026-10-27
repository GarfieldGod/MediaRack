using System.Collections.Concurrent;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Library;

/// <summary>
/// 按需首帧抽帧：把条目视频轨的<b>第 0 帧</b>抽成 JPEG，落在
/// <c>&lt;meta_root&gt;/thumbs/&lt;source&gt;/&lt;key&gt;.jpg</c>。
/// <para>
/// 与封面海报（中点帧、宽 ≤560）是两个东西：首帧是给信息流
/// "滑动到哪、画面就在哪" 的连贯翻页体验用的——落位瞬间缩略图和播放画面无缝衔接。
/// </para>
/// <para>
/// 结构与 <see cref="ExportService"/> 同款：同条目互斥（并发请求等第一个 ffmpeg）、
/// <c>mtime</c> 当 LRU 时钟、按总预算淘汰。写入 meta 目录，源目录一个字节不碰。
/// </para>
/// </summary>
public sealed class ThumbService(string metaRoot, long budgetBytes = ThumbService.DefaultBudgetBytes)
{
    /// <summary>首帧产物的总预算，默认 512 MiB（≈ 一千多张 720p JPEG）。</summary>
    public const long DefaultBudgetBytes = 512L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static string PathFor(string metaRoot, string sourceId, string key) =>
        Path.Combine(metaRoot, "thumbs", SafeSegment(sourceId), key + ".jpg");

    /// <summary>产物已就绪时返回绝对路径；否则 null。</summary>
    public string? TryGetReady(string sourceId, string key)
    {
        var path = PathFor(metaRoot, sourceId, key);
        return File.Exists(path) ? path : null;
    }

    public void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 刷不上不影响返回图片。
        }
    }

    /// <summary>
    /// 阻塞到首帧就绪并返回路径。已存在则直接返回（不重复抽帧）。
    /// </summary>
    /// <exception cref="ExportException">条目不存在、源文件缺失、缺 ffmpeg、或 ffmpeg 失败。</exception>
    public string Ensure(string sourceId, string key, CancellationToken ct = default)
    {
        var existing = TryGetReady(sourceId, key);
        if (existing is not null)
        {
            Touch(existing);
            return existing;
        }

        var gate = Locks.GetOrAdd($"{sourceId}/{key}", _ => new SemaphoreSlim(1, 1));
        gate.Wait(ct);
        try
        {
            existing = TryGetReady(sourceId, key);
            if (existing is not null)
            {
                Touch(existing);
                return existing;
            }

            return Build(sourceId, key, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private string Build(string sourceId, string key, CancellationToken ct)
    {
        using var db = LibraryDatabase.Open(metaRoot, create: false);

        var entry = new MediaRepository(db).Get(sourceId, key)
            ?? throw new ExportException($"条目不存在：{sourceId}/{key}", ExportErrorKind.NotFound);

        var source = new SourceRepository(db).Get(sourceId)
            ?? throw new ExportException($"源未登记：{sourceId}", ExportErrorKind.NotFound);

        var videoPath = MediaLocator.FindPrimaryVideo(
            source, entry.RelPath, entry.Layout, entry.TagCode)
            ?? throw new ExportException($"找不到视频轨：{entry.RelPath}", ExportErrorKind.NotFound);

        if (!File.Exists(videoPath))
        {
            throw new ExportException($"文件不存在：{entry.RelPath}", ExportErrorKind.NotFound);
        }

        var target = PathFor(metaRoot, sourceId, key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var tools = FfTools.Locate();
        var availability = tools.Check();
        if (!availability.Available)
        {
            throw new ExportException(availability.Error ?? "ffmpeg 不可用", ExportErrorKind.NoFfmpeg);
        }

        // -frames:v 1 只解一帧；scale 到高 720（宽自适应保持比例）——信息流全屏够用，
        // 也把 4K 首帧的体积从 ~2MB 压到 ~150KB。
        var temp = target + ".tmp";
        TryDelete(temp);

        var result = tools.RunFfmpeg(
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", videoPath,
            "-frames:v", "1",
            "-vf", "scale=-2:720",
            "-q:v", "3",
            "-f", "image2",
            temp,
        ], ct);

        if (!result.Ok || !File.Exists(temp) || new FileInfo(temp).Length == 0)
        {
            TryDelete(temp);
            throw new ExportException(
                $"ffmpeg 抽帧失败（退出码 {result.ExitCode}）：{Tail(result.StdErr)}",
                ExportErrorKind.Failed);
        }

        File.Move(temp, target, overwrite: true);
        Evict();
        return target;
    }

    /// <summary>按总预算淘汰最久未使用的首帧。返回删除的文件数。</summary>
    public int Evict()
    {
        var root = Path.Combine(metaRoot, "thumbs");
        if (!Directory.Exists(root)) return 0;

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root)
                .EnumerateFiles("*.jpg", SearchOption.AllDirectories)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var total = files.Sum(f => f.Length);
        if (total <= budgetBytes) return 0;

        var removed = 0;
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= budgetBytes) break;

            try
            {
                var size = file.Length;
                file.Delete();
                total -= size;
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 被占用的跳过。
            }
        }

        return removed;
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "（无输出）" : string.Join(" | ", lines.TakeLast(3));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留着，下一轮 -y 会覆盖。
        }
    }

    /// <summary>把源标识压成安全目录名，与封面/导出目录同一规则。</summary>
    private static string SafeSegment(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '_';
        }

        var s = new string(buffer).Trim('_');
        return s.Length == 0 ? "source" : s;
    }
}
