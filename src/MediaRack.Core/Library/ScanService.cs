using System.Diagnostics;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Store;

namespace MediaRack.Library;

/// <summary>
/// 一次完整扫描：扫描 → 入库 → 标缺失 → 同步合集 → 登记源 → 补缩略图。
/// <para>
/// 之所以放在 Core 而不是 CLI 里，是因为服务端的 <c>POST /api/v1/scan</c> 需要<b>同一套顺序</b>。
/// 两处各写一遍的话，"先标缺失还是先写条目"这类顺序问题迟早会出现两个版本。
/// </para>
/// <para>
/// 全过程只读源目录，写入目标只有 <paramref name="db"/> 与 <c>&lt;meta_root&gt;/poster</c>。
/// </para>
/// </summary>
public sealed class ScanService(LibraryDatabase db, string metaRoot)
{
    /// <summary>
    /// 纯扫描，不碰库。用于 <c>--dry-run</c> 与"先看看这个目录能不能识别"。
    /// <para>
    /// v3 起逐文件夹扫描：<see cref="SourceFolder.Layout"/> 跟文件夹走（auto 现场识别），
    /// 条目合并成一份（key 规则不变，rel_path 相对所在文件夹根）。
    /// </para>
    /// </summary>
    /// <param name="progress">逐条进度上报；不传就不报（CLI 与审计不需要）。</param>
    /// <param name="decorateProbe">
    /// single 文件夹的 probe 装饰器：(文件夹, 默认探测器) → 实际探测器。用于给
    /// ffprobe 套一层库预载的 mtime 缓存（<see cref="CachedProbe"/>）；CLI dry-run
    /// 等场景不传即保持直连行为。
    /// </param>
    public static ScanAttempt ScanOnly(
        SourceConfig source,
        VerifyMode verify,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        Func<SourceFolder, IMediaProbe, IMediaProbe>? decorateProbe = null)
    {
        var folders = source.Folders.Count > 0
            ? source.Folders
            : // 扫描作业 / CLI 构造的是"单文件夹视角"的 SourceConfig（只填 Root/Layout，无 Folders）
            (IReadOnlyList<SourceFolder>)(string.IsNullOrEmpty(source.Root)
                ? []
                : [new SourceFolder(0, 0, source.Root, source.Layout)]);

        if (folders.Count == 0)
        {
            return new ScanAttempt(
                new ScanResult(new ScanReport { SourceId = source.Id, Verify = verify }, []),
                null, [], [], new Dictionary<string, string>(StringComparer.Ordinal));
        }

        // dual 文件夹用不到 ffprobe；存在 single 文件夹才做可用性检查。
        var needProbe = folders.Any(f => LayoutDetector.Resolve(f) == SourceLayout.Single);
        FfTools? tools = null;
        FfAvailability? availability = null;
        if (needProbe)
        {
            tools = FfTools.Locate();
            availability = tools.Check();
        }

        var note = availability is { Available: false } ? availability.Error : null;

        var report = new ScanReport { SourceId = source.Id, Verify = verify };
        var entries = new List<MediaEntry>();
        var singleEntries = new List<MediaEntry>();
        // single 条目的 key 是相对路径哈希，指纹认领需要能找回真实文件：
        // key → 该条目所在文件夹的根（指纹只对 single 算，dual 的 key 本身就是内容身份）
        var singleRoots = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            var layout = LayoutDetector.Resolve(folder);
            var scoped = ScopeToFolder(source, folder, layout);

            // 探测后端有两种：ffprobe（本机装了 ffmpeg）和 NullProbe（没装，什么都探不到）。
            // 装饰器必须套在**两种**外面：缓存命中就回放库里的元数据，这件事跟有没有 ffprobe
            // 无关——恰恰是没装 ffprobe 的机器最需要它（库里早有值，不该重扫一遍全变成未知）。
            // 曾经这里是三元的两个分支各造一个 scanner，装饰器只挂在 ffprobe 那支上，
            // 于是"没有 ffprobe ⇒ 缓存一次都不查"：扫描结果 duration/width/height 全 null，
            // 客户端只能显示未知时长（2026-10-30 CI 上被 ProbeCacheTests 抓出来）。
            IMediaProbe inner = availability!.Available ? new FfProbe(tools!) : new NullProbe();

            var result = layout == SourceLayout.Dual
                ? new DualScanner().Scan(scoped, verify, progress, ct)
                : new SingleScanner(decorateProbe?.Invoke(folder, inner) ?? inner)
                    .Scan(scoped, progress, ct);

            report.OkCount += result.Report.OkCount;
            report.CorruptCount += result.Report.CorruptCount;
            report.MissingCount += result.Report.MissingCount;
            report.Problems.AddRange(result.Report.Problems);
            report.Elapsed += result.Report.Elapsed;
            entries.AddRange(result.Entries);
            if (layout == SourceLayout.Single)
            {
                singleEntries.AddRange(result.Entries);
                foreach (var e in result.Entries) singleRoots[e.Key] = folder.Root;
            }
        }

        return new ScanAttempt(
            new ScanResult(report, entries), note, folders, singleEntries, singleRoots);
    }

    /// <summary>构造"单文件夹视角"的 SourceConfig：扫描器只认 Root/Layout，把文件夹伪装成整个源。</summary>
    private static SourceConfig ScopeToFolder(SourceConfig source, SourceFolder folder, SourceLayout layout) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Root = folder.Root,
        Layout = layout,
        VirtualWrite = source.VirtualWrite,
        Enabled = source.Enabled,
        ScanInclude = source.ScanInclude,
        Slug = source.Slug,
        Token = source.Token,
        Folders = [folder],
    };

    /// <summary>
    /// 完整流程。返回的报告体直接给 CLI 排版，全部字段都是"已经做完"的结论。
    /// </summary>
    /// <param name="progress">
    /// 阶段进度上报（扫描 → 写库 → 合集 → 缩略图）。服务端扫描任务靠它给前端画进度条。
    /// </param>
    public ScanOutcome Run(
        SourceConfig source,
        ScanOptions options,
        IProgress<PosterOutcome>? posterProgress = null,
        Action<string>? onPosterUnavailable = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // single 源的 probe 缓存：库里有 mtime + size 的条目，重扫时直接沿用
        // 时长/分辨率/编码，跳过 ffprobe。按文件夹各载一份（rel_path 相对各自根）。
        // ScanOnly 是纯扫描不碰库，缓存由这里（有 db 的一方）注入。
        var decorateProbe = new Func<SourceFolder, IMediaProbe, IMediaProbe>(
            (folder, inner) => new CachedProbe(inner, ProbeCache.Load(db, source.Id, folder.Root!)));

        var attempt = ScanOnly(source, options.Verify, progress, ct, decorateProbe);
        var result = attempt.Result;

        // 扫描阶段被打断就绝不能落库：半份 entries 进 Upsert 后，
        // MarkMissingExcept 会把没扫到的条目全打成 missing——取消不能变成数据事故。
        ct.ThrowIfCancellationRequested();

        // auto 文件夹把识别结果写回，下次扫描不再重复猜。
        foreach (var folder in attempt.Folders)
        {
            if (folder.Id > 0 && folder.Layout is null)
            {
                new SourceRepository(db).SetFolderLayout(
                    source.Id, folder.Id, LayoutDetector.Resolve(folder));
            }
        }

        int written = 0, markedMissing = 0, fpMigrated = 0;
        CollectionSyncResult? collections = null;
        PosterReport? posters = null;
        var posterSeen = 0;

        if (options.Persist)
        {
            var mediaRepo = new MediaRepository(db);

            // 写库是一次批量 upsert，中间没有可报的粒度：只报"开始/结束"两个点。
            progress?.Report(new ScanProgress(ScanStage.Write, 0, result.Entries.Count));

            written = mediaRepo.Upsert(source.Id, result.Entries);
            markedMissing = mediaRepo.MarkMissingExcept(
                source.Id, result.Entries.Select(e => e.Key).ToList());

            // 内容指纹认领：路径动了（整理/移动/换源重加）的新 key 按 fp 找回旧 meta。
            // 放在 MarkMissingExcept 之后：先让"位置失效"尘埃落定，再按指纹认领。
            fpMigrated = FingerprintSync.Sync(db, attempt.SingleFolderEntries, attempt.SingleRoots);

            progress?.Report(new ScanProgress(ScanStage.Write, result.Entries.Count, result.Entries.Count));

            // 合集是 media 的投影：single 按目录、dual 按 UP 主，整库重建一次（幂等）。
            // 条数上万也只是一次分组查询，比维护增量状态简单且不会漂移。
            progress?.Report(new ScanProgress(ScanStage.Collections, 0, 0));
            var rebuilt = new CollectionRebuilder(db).Rebuild();
            collections = new CollectionSyncResult(
                rebuilt.Collections, rebuilt.Bound, rebuilt.Removed, 0);

            new SourceRepository(db).Upsert(
                source,
                itemCount: result.Entries.Count,
                lastScanAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (options.Posters is not null)
            {
                // 抽帧有多少张要跑，只有 PosterService 进到库里才知道；这里只报"已完成数"，
                // total 留 0 —— 前端显示不确定进度条，绝不假装知道总量。
                posterSeen = 0;
                var sink = progress is null
                    ? posterProgress
                    : new DelegateProgress<PosterOutcome>(o =>
                    {
                        var n = Interlocked.Increment(ref posterSeen);
                        progress.Report(new ScanProgress(ScanStage.Posters, n, 0));
                        posterProgress?.Report(o);
                    });

                posters = PosterService.Run(
                    db, source, metaRoot, options.Posters, sink, onPosterUnavailable, ct);
            }
        }

        sw.Stop();
        return new ScanOutcome(
            source, result.Report, written, markedMissing, fpMigrated, collections, posters, attempt.Note, sw.Elapsed);
    }
}

/// <summary>扫描参数。</summary>
/// <param name="Verify">完整性校验强度，仅对 dual 源有效。</param>
/// <param name="Posters">缩略图参数；null 表示本轮不抽帧。</param>
/// <param name="Persist">false 即 dry-run：只扫描，不写库。</param>
public sealed record ScanOptions(
    VerifyMode Verify = VerifyMode.Sample,
    PosterOptions? Posters = null,
    bool Persist = true);

/// <summary>扫描产物 + 需要提示给用户的降级说明（缺 ffprobe 等）。</summary>
/// <param name="Folders">实际参与扫描的文件夹（auto 已解析语义，存储值仍为 auto）。</param>
/// <param name="SingleFolderEntries">
/// single 文件夹产出的条目。合集同步只认物理目录结构，dual 条目不能混进来。
/// </param>
/// <param name="SingleRoots">single 条目 key → 所在文件夹根，指纹认领时定位真实文件用。</param>
public sealed record ScanAttempt(
    ScanResult Result,
    string? Note,
    IReadOnlyList<SourceFolder> Folders,
    IReadOnlyList<MediaEntry> SingleFolderEntries,
    IReadOnlyDictionary<string, string> SingleRoots);

/// <summary>一次完整扫描的结果。</summary>
/// <param name="Written">写入库的条目数（dry-run 时为 0）。</param>
/// <param name="MarkedMissing">本次未出现而被标记 missing 的既有条目数。</param>
/// <param name="FpMigrated">按内容指纹把 meta 从旧位置认领到新位置的条目数。</param>
/// <param name="Collections">合集同步结果，仅 single 源有值。</param>
/// <param name="Posters">抽帧结果；null 表示没抽或 ffmpeg 不可用。</param>
public sealed record ScanOutcome(
    SourceConfig Source,
    ScanReport Report,
    int Written,
    int MarkedMissing,
    int FpMigrated,
    CollectionSyncResult? Collections,
    PosterReport? Posters,
    string? Note,
    TimeSpan Elapsed);
