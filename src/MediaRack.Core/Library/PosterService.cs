using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Library;

/// <summary>
/// 抽帧的执行壳。<c>scan</c> 命令、<c>posters</c> 命令、服务端的扫描任务都走这里，
/// 因此"什么时候抽、抽完怎么回写 <c>poster_rel</c>"只有一份实现。
/// <para>
/// 与 <see cref="PosterGenerator"/> 的分工：生成器只管"把一帧抠出来"，本类管
/// "从库里挑候选 → 跑一轮 → 把结果写回库"，并负责在 ffmpeg 不可用时给出可读提示。
/// </para>
/// </summary>
public sealed class PosterService
{
    /// <summary>
    /// 对一个源跑一轮抽帧。
    /// </summary>
    /// <param name="onUnavailable">
    /// ffmpeg 不可用时的通知回调（CLI 打到 stderr，Server 打到日志）。
    /// 返回 null 表示本轮没跑，调用方应提示而不该报错——缺 ffmpeg 只影响封面，不影响库。
    /// </param>
    public static PosterReport? Run(
        LibraryDatabase db,
        SourceConfig source,
        string metaRoot,
        PosterOptions options,
        IProgress<PosterOutcome>? progress = null,
        Action<string>? onUnavailable = null,
        CancellationToken ct = default)
    {
        var tools = FfTools.Locate();
        var availability = tools.Check();
        if (!availability.Available)
        {
            onUnavailable?.Invoke(availability.Error ?? "ffmpeg 不可用");
            return null;
        }

        var repo = new MediaRepository(db);

        // 取全部 ok 条目而不是只取 poster_rel IS NULL：封面文件可能被手工删掉、或库是从备份恢复的，
        // 以磁盘为准才不会漏。代价只是每条一次 File.Exists。
        var candidates = repo.ListPosterCandidates(source.Id, onlyMissing: false);
        if (candidates.Count == 0) return new PosterReport(0, 0, 0, 0, 0, [], []);

        var report = new PosterGenerator(tools).Run(source, metaRoot, candidates, options, progress, ct);

        if (report.Outcomes.Count > 0)
        {
            repo.UpdatePosters(source.Id, report.Outcomes.Select(o => (o.Key, o.PosterRel)).ToList());
        }

        return report;
    }
}
