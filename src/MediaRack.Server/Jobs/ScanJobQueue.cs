using System.Collections.Concurrent;
using MediaRack.Library;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Server.Hosting;
using MediaRack.Server.Json;
using MediaRack.Store;

namespace MediaRack.Server.Jobs;

/// <summary>
/// 扫描任务表。扫描可能要跑几分钟（full 校验、几万条、抽帧），所以接口必须是
/// "立刻返回 202 + jobId，客户端轮询"。
/// <para>
/// 任务表只在内存里：进程重启后未完成的任务丢失是合理的——扫描本身幂等，重发即可。
/// 已完成的任务保留最近 <see cref="HistoryLimit"/> 条，避免长期运行时无限增长。
/// </para>
/// </summary>
public sealed class ScanJobQueue(
    LibraryAccess lib, ServerOptions options, ILogger<ScanJobQueue> log)
{
    private const int HistoryLimit = 50;

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly DriveGate _gate = new();

    public ScanJobDto Start(ScanRequestDto request)
    {
        var id = Guid.NewGuid().ToString("N")[..12];

        var job = new Job
        {
            Id = id,
            State = "queued",
            SourceId = request.SourceId,
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _jobs[id] = job;
        Trim();

        // 进度从工作线程写、接口线程读：任务字段是 volatile，读取方拿到的是最新一次上报。
        // 用 DelegateProgress 而不是 Progress<T>——后者会把回调丢回捕获的上下文，
        // 这里没有 UI 线程，同步执行就够了。
        var progress = new DelegateProgress<ScanProgress>(p =>
        {
            job.Stage = p.Stage;
            job.Done = p.Done;
            job.Total = p.Total;
            job.Current = p.Current;
        });

        // 明确不 await：接口立刻回 202。任务自己管自己的生命周期与异常。
        _ = Task.Run(() => Execute(job, request, progress, job.Cts.Token));
        return job.ToDto();
    }

    public ScanJobDto? Get(string id) => _jobs.TryGetValue(id, out var job) ? job.ToDto() : null;

    /// <summary>
    /// 请求取消一个任务。running → 置 cancelling 并打断令牌（扫描/抽帧在下个检查点停下）；
    /// queued → 直接置 cancelled（工作线程还没起，不会有人再把它跑起来）。
    /// 已结束的任务原样返回，调用方据此提示"已经结束了"。
    /// </summary>
    public ScanJobDto? Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return null;
        if (job.State is "queued" or "running")
        {
            job.State = job.State == "queued" ? "cancelled" : "cancelling";
            job.Cts.Cancel();
        }
        return job.ToDto();
    }

    /// <summary>
    /// 取消某个源的全部未完成任务（删源前调用）。
    /// <para>
    /// 不取消的话有个很难看的竞态：删源事务刚提交，正在跑的扫描接着把它扫到的条目
    /// Upsert 回来、顺手 Upsert 出源行——用户看到"删了又自己长回来"。
    /// </para>
    /// </summary>
    /// <returns>被打断的任务数。</returns>
    public int CancelForSource(string sourceId)
    {
        var count = 0;
        foreach (var job in _jobs.Values)
        {
            if (!string.Equals(job.SourceId, sourceId, StringComparison.Ordinal)) continue;
            if (job.State is not ("queued" or "running")) continue;

            job.State = job.State == "queued" ? "cancelled" : "cancelling";
            job.Cts.Cancel();
            count++;
        }

        return count;
    }

    public IReadOnlyList<ScanJobDto> Recent() =>
        _jobs.Values.OrderByDescending(j => j.StartedAt).Select(j => j.ToDto()).ToList();

    private void Execute(Job job, ScanRequestDto request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        // 先拿磁盘额度再开扫：每个盘符并发 1（同盘的扫描自动排队），全局上限兜底。
        // 排队期间 job 保持 queued 态，Current 显示等待原因供前端面板展示。
        // 这里本来就是线程池线程，同步阻塞等信号量没有问题。
        SourceConfig source;
        string[] drives;
        try
        {
            source = ResolveSource(request);
            job.Current = WaitingText(source);
            drives = _gate.Acquire(source, ct);
        }
        catch (OperationCanceledException)
        {
            job.State = "cancelled";
            job.Stage = ScanStage.Failed;
            job.Message = "已取消";
            job.Current = null;
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return;
        }
        catch (Exception ex)
        {
            job.State = "failed";
            job.Stage = ScanStage.Failed;
            job.Message = ex.Message;
            job.Current = null;
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            log.LogError(ex, "扫描任务 {JobId} 失败", job.Id);
            return;
        }

        job.Current = null;
        job.State = "running";
        if (ct.IsCancellationRequested)
        {
            job.State = "cancelled";
            job.Message = "已取消";
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _gate.Release(drives);
            return;
        }

        try
        {
            using var db = lib.Open();
            var service = new ScanService(db, options.MetaRoot);

            var outcome = service.Run(
                source,
                new ScanOptions(
                    Verify: ParseVerify(request.Verify),
                    Posters: new PosterOptions(MaxWidth: options.PosterWidth)),
                onPosterUnavailable: message => log.LogWarning("抽帧跳过：{Message}", message),
                progress: progress,
                ct: ct);

            job.Written = outcome.Written;
            job.MarkedMissing = outcome.MarkedMissing;
            job.Message = $"扫描 {outcome.Report.TotalScanned} 条，正常 {outcome.Report.OkCount}，" +
                          $"损坏 {outcome.Report.CorruptCount}；" +
                          (outcome.Posters is { } p ? $"缩略图 +{p.Generated}" : "未抽帧");

            // 收尾时把进度条推到满格：扫描阶段结束后前端还得看得见"这件事做完了"，
            // 而不是停在上一次上报的 87%。
            job.Total = outcome.Report.TotalScanned;
            job.Done = outcome.Report.TotalScanned;
            job.Stage = ScanStage.Done;
            job.Current = null;
            job.State = "done";
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：入库到一半的条目保留（upsert 幂等，重扫即续），不算失败。
            job.State = "cancelled";
            job.Stage = ScanStage.Failed;
            job.Message = "已取消";
            log.LogInformation("扫描任务 {JobId} 被取消", job.Id);
        }
        catch (Exception ex)
        {
            job.State = "failed";
            job.Stage = ScanStage.Failed;
            job.Message = ex.Message;
            log.LogError(ex, "扫描任务 {JobId} 失败", job.Id);
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _gate.Release(drives);
        }
    }

    /// <summary>排队等待时给用户看的一句话，例如「等待磁盘 H: 空闲」。</summary>
    private static string WaitingText(SourceConfig source)
    {
        var drives = DriveGate.DrivesOf(source);
        return drives.Length == 0 ? "排队等待磁盘空闲…" : $"等待磁盘 {string.Join(" + ", drives)} 空闲…";
    }

    /// <summary>
    /// 决定扫哪个源：给了 <c>root</c> 就用它（新源或改路径），否则按 <c>sourceId</c> 查库。
    /// </summary>
    private SourceConfig ResolveSource(ScanRequestDto request)
    {
        if (!string.IsNullOrWhiteSpace(request.Root))
        {
            var root = Path.GetFullPath(request.Root.Trim());
            if (!Directory.Exists(root)) throw new InvalidOperationException($"源目录不存在：{root}");

            var id = string.IsNullOrWhiteSpace(request.SourceId)
                ? Path.GetFileName(root.TrimEnd('\\', '/'))
                : request.SourceId.Trim();

            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("无法推断源标识，请指定 sourceId");

            var existing = lib.FindSource(id);

            return new SourceConfig
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(request.Name) ? (existing?.Name ?? id) : request.Name.Trim(),
                Layout = ParseLayout(request.Layout, root),
                // 重新扫描不改动已有的虚拟写权限：那是用户设定，不该被一次扫描重置。
                VirtualWrite = existing?.VirtualWrite ?? VirtualWrite.Full,
                Enabled = existing?.Enabled ?? true,
                Root = root,
            };
        }

        if (string.IsNullOrWhiteSpace(request.SourceId))
        {
            throw new InvalidOperationException("请提供 sourceId（扫描已登记的源）或 root（登记新源）");
        }

        return lib.FindSource(request.SourceId.Trim())
            ?? throw new InvalidOperationException($"源未登记：{request.SourceId}");
    }

    private static SourceLayout ParseLayout(string? value, string root) => value?.ToLowerInvariant() switch
    {
        null or "" or "auto" => LayoutDetector.Detect(root),
        "dual" => SourceLayout.Dual,
        "single" => SourceLayout.Single,
        _ => throw new InvalidOperationException($"layout 只接受 auto / dual / single，收到 {value}"),
    };

    private static VerifyMode ParseVerify(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "sample" => VerifyMode.Sample,
        "none" => VerifyMode.None,
        "full" => VerifyMode.Full,
        _ => throw new InvalidOperationException($"verify 只接受 none / sample / full，收到 {value}"),
    };

    private void Trim()
    {
        if (_jobs.Count <= HistoryLimit) return;

        var stale = _jobs.Values
            .Where(j => j.State is "done" or "failed" or "cancelled")
            .OrderBy(j => j.StartedAt)
            .Take(_jobs.Count - HistoryLimit)
            .Select(j => j.Id)
            .ToList();

        foreach (var id in stale) _jobs.TryRemove(id, out _);
    }

    /// <summary>
    /// 按盘符的扫描闸门：每个盘符（含 UNC 共享）并发 1，全局上限 <see cref="MaxConcurrent"/>。
    /// <para>
    /// 同盘的第二个扫描会在信号量上自然排队——等同盘正扫的结束就轮到它，无需额外队列结构。
    /// 多盘符的源按盘符字典序逐个获取，保证两个跨盘任务不会互相抱死（无循环等待）。
    /// </para>
    /// </summary>
    private sealed class DriveGate
    {
        /** 全局并发上限：纯按盘符放开时，多块盘同时扫会让 ffprobe/ffmpeg 进程数失控。 */
        private const int MaxConcurrent = 3;

        private readonly SemaphoreSlim _global = new(MaxConcurrent, MaxConcurrent);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _drives = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>阻塞拿到全局 + 全部涉及盘的额度；取消时抛 OperationCanceledException。</summary>
        /// <returns>本次占用的盘符列表（Release 时按原样归还）。</returns>
        public string[] Acquire(SourceConfig source, CancellationToken ct)
        {
            var drives = DrivesOf(source);

            _global.Wait(ct);
            var taken = new List<SemaphoreSlim>(drives.Length);
            try
            {
                foreach (var drive in drives)
                {
                    var sem = _drives.GetOrAdd(drive, _ => new SemaphoreSlim(1, 1));
                    sem.Wait(ct);
                    taken.Add(sem);
                }
                return drives;
            }
            catch
            {
                // 半途取消/出错：把已拿到的一个个还回去，全局额度也归还。
                foreach (var sem in taken) sem.Release();
                _global.Release();
                throw;
            }
        }

        public void Release(string[] drives)
        {
            foreach (var drive in drives)
            {
                if (_drives.TryGetValue(drive, out var sem)) sem.Release();
            }
            _global.Release();
        }

        /// <summary>源涉及的全部盘符/UNC 前缀，去重、按字典序（多盘源按固定顺序获取，避免循环等待）。</summary>
        public static string[] DrivesOf(SourceConfig source) =>
            (source.Folders.Count > 0
                ? source.Folders.Select(f => f.Root)
                : [source.Root ?? ""])
            .Select(DriveOf)
            .Where(d => d is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        /// <summary>"H:\data" → "H:"；UNC "\\srv\share" → "\\srv\share"（整段当一个排队单位）。非法路径返回 null。</summary>
        private static string? DriveOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path.Trim()));
                return string.IsNullOrEmpty(root) ? null : root.TrimEnd('\\', '/');
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed class Job
    {
        public required string Id { get; init; }
        public volatile string State = "queued";
        public string? SourceId { get; init; }
        public string? Message;

        /** 取消用：Cancel() 打断令牌，扫描/抽帧循环在检查点停下。 */
        public readonly CancellationTokenSource Cts = new();

        // ── 进度 ──
        // 由工作线程写、由接口线程读。volatile 保证读到的是最新一次上报而不是缓存副本；
        // 三个数之间可能短暂不一致（读到新 Done 配旧 Stage），这只影响一帧显示，不上锁。
        public volatile string Stage = ScanStage.Queued;
        public volatile int Done;
        public volatile int Total;
        public volatile string? Current;

        public int Written;
        public int MarkedMissing;
        public long? StartedAt { get; init; }
        public long? FinishedAt;

        public ScanJobDto ToDto() => new(
            JobId: Id,
            State: State,
            SourceId: SourceId,
            Message: Message,
            Written: Written,
            MarkedMissing: MarkedMissing,
            StartedAt: StartedAt,
            FinishedAt: FinishedAt,
            Stage: Stage,
            Done: Done,
            Total: Total,
            Current: Current);
    }
}
