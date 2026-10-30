namespace MediaRack.Media;

using MediaRack.Models;

/// <summary>
/// 缩略图队列的"工人"。扫描只负责入库（<c>poster_rel = NULL</c>），这里后置补图，
/// 于是抽帧慢、抽帧失败都不会拖住扫描——见 docs/design.md §6。
/// <para>
/// 只写 <c>&lt;meta_root&gt;/poster/...</c>，源目录永不触碰。
/// </para>
/// </summary>
public sealed class PosterGenerator(FfTools tools)
{
    private readonly PosterMaker _maker = new(tools);

    /// <summary>封面在 meta 根下的相对路径。用 <c>sourceId/key</c> 分目录，便于按源整体清理。</summary>
    public static string PosterRelativePath(string sourceId, string key) =>
        $"poster/{SafeSegment(sourceId)}/{key}.jpg";

    /// <summary>把相对路径还原成绝对路径（仅 meta 目录内）。</summary>
    public static string ToAbsolute(string metaRoot, string rel) =>
        Path.Combine(metaRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// 处理一批候选。
    /// <para>
    /// <b>以磁盘为准，而不是以库里的记录为准</b>：<c>poster_rel</c> 指向的文件可能已被删除
    /// （用户清了 <c>meta/poster</c>，或库是从备份恢复的）。因此"有没有封面"一律看文件是否存在，
    /// 库里只记录路径。这样删掉封面目录后重跑就能自愈，不需要额外的修复命令。
    /// </para>
    /// </summary>
    public PosterReport Run(
        SourceConfig source,
        string metaRoot,
        IReadOnlyList<PosterCandidate> candidates,
        PosterOptions options,
        IProgress<PosterOutcome>? progress = null,
        CancellationToken ct = default)
    {
        var outcomes = new List<PosterOutcome>();
        var problems = new List<PosterProblem>();
        var work = new List<WorkItem>();

        int skipped = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var rel = string.IsNullOrWhiteSpace(candidate.PosterRel)
                ? PosterRelativePath(source.Id, candidate.Key)
                : candidate.PosterRel;

            var absolute = ToAbsolute(metaRoot, rel);

            if (!options.Force && File.Exists(absolute))
            {
                // 文件在，但库里没登记——顺手补一行，下一轮就不用再查磁盘。
                if (string.IsNullOrWhiteSpace(candidate.PosterRel))
                {
                    var recovered = new PosterOutcome(candidate.Key, rel, PosterOutcomeKind.Recovered);
                    outcomes.Add(recovered);
                    progress?.Report(recovered);
                }
                else
                {
                    skipped++;
                }

                continue;
            }

            work.Add(new WorkItem(candidate, rel, absolute));
        }

        int generated = 0, failed = 0, remaining = 0;

        foreach (var item in work)
        {
            ct.ThrowIfCancellationRequested();

            if (options.Limit is { } limit && generated >= limit)
            {
                remaining = work.Count - (generated + failed);
                break;
            }

            var videoPath = MediaLocator.FindPrimaryVideo(
                source, item.Candidate.RelPath, item.Candidate.Layout, item.Candidate.TagCode);

            if (videoPath is null)
            {
                failed++;
                problems.Add(new PosterProblem(item.Candidate.Key, "找不到可抽帧的视频轨"));
                continue;
            }

            if (_maker.TryMake(videoPath, item.Candidate.DurationMs, item.Absolute, out var error, options.MaxWidth))
            {
                generated++;
                var outcome = new PosterOutcome(item.Candidate.Key, item.Rel, PosterOutcomeKind.Generated);
                outcomes.Add(outcome);
                progress?.Report(outcome);
            }
            else
            {
                failed++;
                problems.Add(new PosterProblem(item.Candidate.Key, error ?? "抽帧失败"));
            }
        }

        return new PosterReport(generated, skipped, failed, remaining, work.Count, outcomes, problems);
    }

    private sealed record WorkItem(PosterCandidate Candidate, string Rel, string Absolute);

    /// <summary>把源标识压成安全目录名：只保留字母数字与 <c>-_</c>。</summary>
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

/// <summary>抽帧参数。</summary>
/// <param name="MaxWidth">缩略图最大宽度（不放大）。</param>
/// <param name="Limit">本轮最多生成几张；null 表示不限制。</param>
/// <param name="Force">已有文件也重新生成。</param>
public sealed record PosterOptions(int MaxWidth = PosterMaker.DefaultMaxWidth, int? Limit = null, bool Force = false);

/// <summary>单条缩略图的处理结果。</summary>
public enum PosterOutcomeKind
{
    /// <summary>本次新抽帧生成。</summary>
    Generated,

    /// <summary>文件已存在但库里没登记，本次补登记。</summary>
    Recovered,
}

/// <summary>一条缩略图产物，调用方据此更新 <c>poster_rel</c>。</summary>
public sealed record PosterOutcome(string Key, string PosterRel, PosterOutcomeKind Kind);

/// <summary>一条抽帧失败记录。</summary>
public sealed record PosterProblem(string Key, string Message);

/// <summary>一轮抽帧的汇总。</summary>
/// <param name="Generated">本次新生成数量。</param>
/// <param name="Skipped">已有封面且文件存在，跳过数量。</param>
/// <param name="Failed">失败数量。</param>
/// <param name="Remaining">因 <see cref="PosterOptions.Limit"/> 未处理的数量。</param>
/// <param name="Pending">本轮候选总数。</param>
public sealed record PosterReport(
    int Generated,
    int Skipped,
    int Failed,
    int Remaining,
    int Pending,
    IReadOnlyList<PosterOutcome> Outcomes,
    IReadOnlyList<PosterProblem> Problems);
