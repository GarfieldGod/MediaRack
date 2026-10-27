using MediaRack.Library;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Cli;

/// <summary>
/// 缩略图相关的 CLI 外壳：只负责"进度打到 stderr、报告排到 stdout"，
/// 真正的编排在 <see cref="PosterService"/>——服务端用的是同一份。
/// </summary>
internal static class PosterRunner
{
    /// <summary>
    /// 对一个源跑一轮抽帧。返回 null 表示环境不可用（缺 ffmpeg），调用方应提示而非报错。
    /// </summary>
    public static PosterReport? Execute(
        LibraryDatabase db,
        SourceConfig source,
        string metaTarget,
        PosterOptions options,
        CancellationToken ct = default)
    {
        // 进度走 stderr：大库抽帧要跑很久，但 stdout 必须留给最终报告。
        var done = 0;
        var progress = new Progress<PosterOutcome>(_ =>
        {
            done++;
            if (done % 50 == 0) Console.Error.WriteLine($"  …已生成 {done} 张");
        });

        var metaRoot = Path.GetDirectoryName(LibraryDatabase.ResolvePath(metaTarget)) ?? metaTarget;

        return PosterService.Run(
            db, source, metaRoot, options, progress, Console.Error.WriteLine, ct);
    }

    /// <summary>一行摘要，供 scan / posters 的报告体共用。</summary>
    public static string Summarize(PosterReport report)
    {
        var text = $"待生成 {report.Pending} 条 → 生成 {report.Generated}，跳过 {report.Skipped}，失败 {report.Failed}";
        return report.Remaining > 0 ? text + $"，剩余 {report.Remaining}（下轮继续）" : text;
    }

    /// <summary>失败明细（最多 5 条），没有失败时返回空。</summary>
    public static IEnumerable<string> FailureLines(PosterReport report)
    {
        foreach (var p in report.Problems.Take(5))
        {
            yield return $"[失败] {p.Key}  {p.Message}";
        }

        if (report.Problems.Count > 5)
        {
            yield return $"…… 另有 {report.Problems.Count - 5} 条失败未显示";
        }
    }
}
