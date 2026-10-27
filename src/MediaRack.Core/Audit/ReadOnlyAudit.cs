using MediaRack.Library;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Audit;

/// <summary>审计参数。</summary>
/// <param name="Verify">dual 源的完整性校验强度。</param>
/// <param name="Fingerprint">快照强度。</param>
/// <param name="MaxPosters">最多抽几张帧（抽帧是唯一会读满整段视频的操作，要限流）。</param>
public sealed record AuditOptions(
    VerifyMode Verify = VerifyMode.Sample,
    FingerprintMode Fingerprint = FingerprintMode.Metadata,
    int MaxPosters = 20);

/// <summary>审计中的一个步骤。<c>Ok = false</c> 会让整次审计判为不通过。</summary>
public sealed record AuditStep(string Name, string Detail, bool Ok);

/// <summary>审计结果。<c>Passed</c> 的判据是「快照 diff 为空」且「所有步骤都通过」。</summary>
public sealed record AuditReport(
    SourceConfig Source,
    SourceSnapshot Before,
    SourceSnapshot After,
    SnapshotDiff Diff,
    IReadOnlyList<AuditStep> Steps,
    IReadOnlyList<string> Notes)
{
    public bool Passed => Diff.IsEmpty && Steps.All(s => s.Ok);
}

/// <summary>
/// 只读性的<b>机械证明</b>。
/// <para>
/// 思路很直白：先把源目录整体拍一份指纹快照，然后让程序把一整套真实操作全跑一遍
/// （扫描、抽帧、改名、标签、隐藏、收藏、策略闸门、重扫），最后再拍一次快照比对。
/// </para>
/// <para>
/// 之所以要"跑一遍"而不是"看看代码里有没有写文件"：后者只能证明当下没人写，
/// 任何一次重构都可能悄悄引入写操作。前者可以在每次改动后重跑，永远给出同一个结论。
/// </para>
/// <para>
/// 审计使用<b>临时库</b>，不碰用户正在用的库；临时目录在结束时删除。
/// </para>
/// </summary>
public static class ReadOnlyAudit
{
    public static AuditReport Run(
        SourceConfig source, AuditOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AuditOptions();

        var steps = new List<AuditStep>();
        var notes = new List<string>();

        var before = SourceSnapshot.Take(RootsOf(source), options.Fingerprint, ct);

        var tempRoot = Path.Combine(
            Path.GetTempPath(), "mrack-audit-" + Guid.NewGuid().ToString("N")[..8]);

        SourceSnapshot after;
        try
        {
            Directory.CreateDirectory(tempRoot);

            using var db = LibraryDatabase.Open(tempRoot);
            db.Migrate();

            var media = new MediaRepository(db);
            var sources = new SourceRepository(db);
            var meta = new MetaRepository(db);
            var favorites = new FavoriteRepository(db);

            // ── 1. 扫描 ────────────────────────────────────────
            var attempt = ScanSource(source, options.Verify, ct);
            var scan = attempt.Result;
            var written = media.Upsert(source.Id, scan.Entries);
            var markedMissing = media.MarkMissingExcept(
                source.Id, scan.Entries.Select(e => e.Key).ToList());
            sources.Upsert(source, scan.Entries.Count, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            steps.Add(new AuditStep(
                "扫描源目录",
                $"{scan.Report.TotalScanned} 条（正常 {scan.Report.OkCount} / 损坏 {scan.Report.CorruptCount}），" +
                $"写库 {written} 条，标记缺失 {markedMissing} 条",
                scan.Report.TotalScanned > 0));

            // ── 2. 条目 → 合集（single 目录 / dual UP 主，排他投影）──
            var rebuilt = new CollectionRebuilder(db).Rebuild();
            steps.Add(new AuditStep(
                "重建合集",
                $"{rebuilt.Collections} 个合集，绑定 {rebuilt.Bound} 条",
                rebuilt.Bound == scan.Entries.Count));

            // ── 3. 抽帧（会读整段视频，最像"写"的嫌疑操作）─────
            var posters = RunPosters(db, media, source, tempRoot, options.MaxPosters, ct, notes);
            if (posters is not null)
            {
                // 抽帧失败（比如源里躺着一个改名的文本文件）不是只读性问题，
                // 所以这一项只要"跑完了"就算通过，失败数只写进详情供人判断。
                steps.Add(new AuditStep(
                    "抽帧写缩略图",
                    $"待处理 {posters.Pending} 条 → 生成 {posters.Generated}、失败 {posters.Failed}" +
                    "（写入 meta 目录，不在源目录）",
                    true));
            }

            // ── 4. 虚拟写入：每个条目都改一遍 ──────────────────
            var keys = scan.Entries.Select(e => e.Key).ToList();
            var applied = 0;
            foreach (var (key, index) in keys.Select((k, i) => (k, i)))
            {
                var patch = new MetaPatch
                {
                    TitleDisplay = $"审计改名 #{index}",
                    TagsAdd = ["审计", index % 2 == 0 ? "偶数" : "奇数"],
                    Hidden = index % 5 == 0,
                    Rotation = index % 4 == 0 ? 90 : 0,
                    PlayPosMs = index * 1000L,
                    MarkedPlayed = index == 0,
                };

                if (meta.Apply(source.Id, key, patch, actor: "verify-readonly").Ok) applied++;
            }

            steps.Add(new AuditStep(
                "虚拟改名 / 标签 / 隐藏 / 旋转 / 进度",
                $"尝试 {keys.Count} 条，成功 {applied} 条（全部只写库）",
                applied == keys.Count));

            // ── 5. 收藏（用户层，多对多，另一条写路径）───────────
            var favoriteId = favorites.Create("审计收藏夹");
            var added = favorites.AddItems(
                favoriteId, keys.Select(k => new FavoriteRepository.ItemRef(source.Id, k)).ToList());
            favorites.Rename(favoriteId, "审计收藏夹（改名）");
            var removedItems = keys.Count > 1
                ? favorites.RemoveItems(favoriteId, [new FavoriteRepository.ItemRef(source.Id, keys[0])])
                : 0;

            steps.Add(new AuditStep(
                "新建收藏夹 / 加入 / 改名 / 移除",
                $"加入 {added} 条，移除 {removedItems} 条",
                added == keys.Count && removedItems == (keys.Count > 1 ? 1 : 0)));

            // ── 6. 重扫：虚拟元数据必须存活，且扫描不该动磁盘 ───
            var rescan = ScanSource(source, options.Verify, ct).Result;
            media.Upsert(source.Id, rescan.Entries);

            var survived = keys.Count(k => meta.Get(source.Id, k) is { Rev: > 0 });
            steps.Add(new AuditStep(
                "重扫后虚拟元数据存活",
                $"{survived}/{keys.Count} 条仍带 rev（扫描不覆盖虚拟层）",
                survived == keys.Count));

            // ── 7. 策略闸门：三档写权限的真值表 ─────────────────
            var gate = CheckPolicyGate(sources, meta, source, keys.FirstOrDefault());
            steps.Add(gate ?? new AuditStep("策略闸门", "无条目可测", false));

            // 收尾：把临时库的源权限恢复成调用方给的值，便于其他步骤复算。
            sources.SetVirtualWrite(source.Id, source.VirtualWrite);

            after = SourceSnapshot.Take(RootsOf(source), options.Fingerprint, ct);
        }
        finally
        {
            TryDelete(tempRoot);
        }

        var diff = SnapshotDiff.Compare(before, after);
        return new AuditReport(source, before, after, diff, steps, notes);
    }

    /// <summary>三档 <see cref="VirtualWrite"/> 的行为必须与设计一致，否则权限只是装饰。</summary>
    private static AuditStep? CheckPolicyGate(
        SourceRepository sources, MetaRepository meta, SourceConfig source, string? key)
    {
        if (key is null) return null;

        var title = new MetaPatch { TitleDisplay = "闸门测试" };
        var tags = new MetaPatch { TagsAdd = ["闸门"] };

        sources.SetVirtualWrite(source.Id, VirtualWrite.None);
        var noneTitle = meta.Apply(source.Id, key, title).Status;
        var noneTags = meta.Apply(source.Id, key, tags).Status;

        sources.SetVirtualWrite(source.Id, VirtualWrite.Whitelist);
        var wlTitle = meta.Apply(source.Id, key, title).Status;
        var wlTags = meta.Apply(source.Id, key, tags).Status;

        sources.SetVirtualWrite(source.Id, VirtualWrite.Full);
        var fullTitle = meta.Apply(source.Id, key, title).Status;

        var ok = noneTitle == WriteStatus.Forbidden && noneTags == WriteStatus.Forbidden &&
                 wlTitle == WriteStatus.Forbidden && wlTags == WriteStatus.Applied &&
                 fullTitle == WriteStatus.Applied;

        var detail =
            $"none: 标题 {Zh(noneTitle)} / 标签 {Zh(noneTags)}；" +
            $"whitelist: 标题 {Zh(wlTitle)} / 标签 {Zh(wlTags)}；" +
            $"full: 标题 {Zh(fullTitle)}";

        return new AuditStep("策略闸门（三档真值表）", detail, ok);
    }

    private static string Zh(WriteStatus status) => status switch
    {
        WriteStatus.Applied => "允许",
        WriteStatus.Forbidden => "拒绝",
        WriteStatus.Conflict => "冲突",
        _ => "未找到",
    };

    private static PosterReport? RunPosters(
        LibraryDatabase db, MediaRepository media, SourceConfig source, string metaRoot,
        int maxPosters, CancellationToken ct, List<string> notes)
    {
        var tools = FfTools.Locate();
        var availability = tools.Check();
        if (!availability.Available)
        {
            notes.Add($"跳过抽帧：{availability.Error}");
            return null;
        }

        var candidates = media.ListPosterCandidates(source.Id, onlyMissing: false);
        if (candidates.Count == 0) return new PosterReport(0, 0, 0, 0, 0, [], []);

        var report = new PosterGenerator(tools).Run(
            source, metaRoot, candidates,
            new PosterOptions(Limit: maxPosters), progress: null, ct);

        if (report.Outcomes.Count > 0)
        {
            media.UpdatePosters(source.Id, report.Outcomes.Select(o => (o.Key, o.PosterRel)).ToList());
        }

        return report;
    }

    /// <summary>复用 ScanService 的多文件夹扫描逻辑，不再自己维护一份按布局分派的老代码。</summary>
    private static ScanAttempt ScanSource(SourceConfig source, VerifyMode verify, CancellationToken ct) =>
        ScanService.ScanOnly(source, verify, ct: ct);

    /// <summary>快照与审计都按"文件夹列表"取根；单文件夹视角的 SourceConfig 只填 Root。</summary>
    private static IReadOnlyList<string> RootsOf(SourceConfig source)
    {
        if (source.Folders.Count > 0) return source.Folders.Select(f => f.Root).ToList();
        return string.IsNullOrEmpty(source.Root) ? [] : [source.Root];
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不影响结论，最多留一份垃圾。
        }
    }
}
