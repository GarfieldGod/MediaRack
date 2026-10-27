using System.Collections.Concurrent;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Library;

/// <summary>
/// 按需转封装：把 <c>video.m4s</c> + <c>audio.m4s</c> 合成一个单流 mp4，落在
/// <c>&lt;meta_root&gt;/export/&lt;source&gt;/&lt;key&gt;.mp4</c>。
/// <para>
/// <b>这是逃生舱，不是主路径。</b>主路径永远是客户端直接拉两条流（零 CPU）。只有消费方
/// 不认双流时才来这里：浏览器 <c>&lt;video&gt;</c>（一个标签只能吃一条流）、投屏、剪辑、分享。
/// </para>
/// <para>
/// <c>-c copy</c> 只重排容器，不重新编码，因此 4K 也只能跑到磁盘 IO 的带宽。代价是
/// 产物占空间，所以按总预算做 LRU 淘汰；<c>mtime</c> 当作"最近使用时间"来用。
/// </para>
/// <para>
/// 写入目标是 meta 目录，源目录依然一个字节都不碰。
/// </para>
/// </summary>
public sealed class ExportService(string metaRoot, long budgetBytes = ExportService.DefaultBudgetBytes)
{
    /// <summary>导出产物的总预算，默认 20 GiB。</summary>
    public const long DefaultBudgetBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>同一条目同时只允许一个 ffmpeg；后来的请求等它，而不是重复转一份。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static string ExportRoot(string metaRoot) => Path.Combine(metaRoot, "export");

    public static string PathFor(string metaRoot, string sourceId, string key) =>
        Path.Combine(ExportRoot(metaRoot), SafeSegment(sourceId), key + ".mp4");

    /// <summary>产物已就绪时返回绝对路径；否则 null。</summary>
    public string? TryGetReady(string sourceId, string key)
    {
        var path = PathFor(metaRoot, sourceId, key);
        return File.Exists(path) ? path : null;
    }

    /// <summary>把 <c>mtime</c> 刷新成现在，供 LRU 判定"最近用过"。</summary>
    public void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 刷不上不影响播放，淘汰时可能被判成旧的，可以接受。
        }
    }

    /// <summary>
    /// 阻塞到产物就绪并返回路径。已存在则直接返回（不重复转封装）。
    /// </summary>
    /// <exception cref="ExportException">条目不存在、缺 ffmpeg、或 ffmpeg 执行失败。</exception>
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
            // 排队期间别人可能已经转好了。
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

    /// <summary>
    /// 只启动后台构建，立刻返回。<c>POST</c> 导出接口用它实现 <c>202 Accepted</c>。
    /// </summary>
    public void StartBackground(string sourceId, string key, Action<Exception>? onError = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Run(() => Ensure(sourceId, key)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        });
    }

    /// <summary>产物是否正在构建（用于区分 202 与 404）。</summary>
    public bool IsBuilding(string sourceId, string key)
    {
        var gate = Locks.TryGetValue($"{sourceId}/{key}", out var found) ? found : null;
        return gate is not null && gate.CurrentCount == 0;
    }

    /// <summary>
    /// 按总预算淘汰最久未使用的产物。返回删除的文件数。
    /// <para>正在构建的 <c>.tmp</c> 不在扫描范围内，所以不会删掉半成品。</para>
    /// </summary>
    public int Evict()
    {
        var root = ExportRoot(metaRoot);
        if (!Directory.Exists(root)) return 0;

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root)
                .EnumerateFiles("*.mp4", SearchOption.AllDirectories)
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
                // 文件被占用（可能正在被播放）就跳过，下次再淘汰。
            }
        }

        return removed;
    }

    private string Build(string sourceId, string key, CancellationToken ct)
    {
        // 每次自己开连接：ExportService 是单例，不能把连接当字段持有。
        using var db = LibraryDatabase.Open(metaRoot, create: false);

        var entry = new MediaRepository(db).Get(sourceId, key)
            ?? throw new ExportException($"条目不存在：{sourceId}/{key}", ExportErrorKind.NotFound);

        var source = new SourceRepository(db).Get(sourceId)
            ?? throw new ExportException($"源未登记：{sourceId}", ExportErrorKind.NotFound);

        var videoPath = MediaLocator.FindPrimaryVideo(
            source, entry.RelPath, entry.Layout, entry.TagCode)
            ?? throw new ExportException($"找不到视频轨：{entry.RelPath}", ExportErrorKind.NotFound);

        var target = PathFor(metaRoot, sourceId, key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // 单流源本来就是单文件，直接指过去，不复制一份——复制只会在 meta 目录里
        // 多出一份同样大的副本，而导出接口的语义是"给我一个能播的单流"。
        if (entry.Layout == SourceLayout.Single)
        {
            if (!File.Exists(videoPath))
            {
                throw new ExportException($"文件不存在：{entry.RelPath}", ExportErrorKind.NotFound);
            }
            return videoPath;
        }

        var audioPath = Path.Combine(Path.GetDirectoryName(videoPath)!, "audio.m4s");
        if (!File.Exists(audioPath))
        {
            // 只剩视频轨也能放，只是没声音；比直接失败好。
            audioPath = string.Empty;
        }

        var tools = FfTools.Locate();
        var availability = tools.Check();
        if (!availability.Available)
        {
            throw new ExportException(availability.Error ?? "ffmpeg 不可用", ExportErrorKind.NoFfmpeg);
        }

        // 临时文件用 .mp4.tmp，并且显式 -f mp4：ffmpeg 无法从 .tmp 推断容器格式，
        // 不指定就会报 "Unable to find a suitable output format"。
        var temp = target + ".tmp";
        TryDelete(temp);

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", videoPath,
        };

        if (audioPath.Length > 0) args.AddRange(["-i", audioPath]);

        args.AddRange(["-map", "0:v:0"]);
        if (audioPath.Length > 0) args.AddRange(["-map", "1:a:0"]);

        args.AddRange([
            "-c", "copy",
            "-movflags", "+faststart",
            "-f", "mp4",
            temp,
        ]);

        var result = tools.RunFfmpeg(args, ct);
        if (!result.Ok)
        {
            TryDelete(temp);
            throw new ExportException(
                $"ffmpeg 转封装失败（退出码 {result.ExitCode}）：{Tail(result.StdErr)}",
                ExportErrorKind.Failed);
        }

        if (!File.Exists(temp))
        {
            throw new ExportException("ffmpeg 返回成功但没有产出文件", ExportErrorKind.Failed);
        }

        File.Move(temp, target, overwrite: true);
        Evict();
        return target;
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
            // 删不掉就让它留着，下一轮 -y 会覆盖。
        }
    }

    /// <summary>把源标识压成安全目录名，与封面目录保持同一规则。</summary>
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

/// <summary>转封装失败。调用方按 <see cref="Kind"/> 映射状态码，而不是去匹配异常文本。</summary>
public sealed class ExportException(string message, ExportErrorKind kind, Exception? inner = null)
    : Exception(message, inner)
{
    public ExportErrorKind Kind { get; } = kind;
}

public enum ExportErrorKind
{
    /// <summary>条目或源文件不存在 → 404。</summary>
    NotFound,

    /// <summary>缺 ffmpeg → 409，客户端应改用 direct。</summary>
    NoFfmpeg,

    /// <summary>ffmpeg 执行失败 → 500。</summary>
    Failed,
}
