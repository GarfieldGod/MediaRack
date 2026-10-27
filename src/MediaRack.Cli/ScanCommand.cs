using MediaRack.Library;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Store;

namespace MediaRack.Cli;

internal static class ScanCommand
{
    public const string HelpText =
        """
        mrack scan — 扫描一个源目录

        用法：
          mrack scan --root <目录> [选项]

        选项：
          --root <目录>      源目录
          --source <id>      源标识，默认取目录名
          --name <名称>      显示名，默认同 id
          --layout <布局>    auto | dual | single（默认 auto）
                             dual   B 站缓存镜像（<avid>/c_<cid>/<tag>/video.m4s + audio.m4s）
                             single 普通视频文件（任意层级，子目录即合集）
          --verify <模式>    none | sample | full（默认 sample，仅对 dual 有效）
                             none   只查文件存在
                             sample 追加字节数比对
                             full   追加 md5 全量比对（慢）
          --no-posters       跳过缩略图生成
          --poster-width <n> 缩略图最大宽度（默认 560，不放大）
          --poster-limit <n> 本轮最多生成几张
          --dry-run          只扫描，不写库
          --meta <目录|db>   元数据目录

        说明：
          扫描只读源目录，不会创建或修改其中任何文件。
          本次未出现的既有条目标记为 missing，而不是删除。
          缩略图后置生成：失败只留空封面，不影响条目入库。
          single 源的物理目录会同步为合集；合集是视图，磁盘不动。
        """;

    public static int Run(string[] args, string metaTarget)
    {
        var options = Options.Parse(args);
        if (options.Flag("help"))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var rootInput = options.Require("root");
        var root = Path.GetFullPath(rootInput);
        if (!Directory.Exists(root))
        {
            throw new CliException($"源目录不存在：{root}");
        }

        var sourceId = options.Get("source");
        if (string.IsNullOrWhiteSpace(sourceId)) sourceId = Path.GetFileName(root.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(sourceId)) throw new CliException("无法推断源标识，请用 --source 指定");

        var layout = ParseLayout(options.Get("layout"), root);
        var verify = ParseVerify(options.Get("verify"));
        var dryRun = options.Flag("dry-run");
        var withPosters = !options.Flag("no-posters");

        var source = new SourceConfig
        {
            Id = sourceId,
            Name = string.IsNullOrWhiteSpace(options.Get("name")) ? sourceId : options.Get("name")!,
            Layout = layout,
            Root = root,
        };

        using var db = LibraryDatabase.Open(metaTarget);
        db.Migrate();

        var scanOptions = new ScanOptions(
            Verify: verify,
            Posters: withPosters
                ? new PosterOptions(
                    MaxWidth: ParsePositive(options.Get("poster-width"), PosterMaker.DefaultMaxWidth),
                    Limit: ParseOptionalPositive(options.Get("poster-limit")))
                : null,
            Persist: !dryRun);

        var outcome = new ScanService(db, metaTarget).Run(
            source, scanOptions,
            onPosterUnavailable: Console.Error.WriteLine);

        PrintReport(outcome, dryRun);
        return 0;
    }

    private static void PrintReport(ScanOutcome outcome, bool dryRun)
    {
        var source = outcome.Source;
        var report = outcome.Report;

        Console.WriteLine($"源        {source.Id}  ({LayoutName(source.Layout)})");
        Console.WriteLine($"目录      {source.Root}");
        Console.WriteLine($"校验      {VerifyName(report.Verify, source.Layout)}");

        if (dryRun) Console.WriteLine("模式      dry-run（未写入库）");
        if (outcome.Note is not null) Console.WriteLine($"注意      {outcome.Note}");

        Console.WriteLine();
        Console.WriteLine($"扫描      {report.TotalScanned} 个条目");
        Console.WriteLine($"  正常    {report.OkCount}");
        Console.WriteLine($"  损坏    {report.CorruptCount}");
        Console.WriteLine($"  缺失    {report.MissingCount}");

        if (!dryRun)
        {
            Console.WriteLine($"写入      {outcome.Written} 条");
            Console.WriteLine($"标记缺失  {outcome.MarkedMissing} 条（本次未出现）");
            if (outcome.FpMigrated > 0)
            {
                Console.WriteLine($"指纹认领  {outcome.FpMigrated} 条（按内容找回移动/改名条目的设置）");
            }

            if (outcome.Collections is { } collections)
            {
                Console.WriteLine($"合集      {collections.Collections} 个（条目关联 {collections.Items} 条" +
                                  (collections.RemovedCollections > 0
                                      ? $"，移除陈旧 {collections.RemovedCollections} 个"
                                      : string.Empty) +
                                  "）");
            }

            if (outcome.Posters is { } posters)
            {
                Console.WriteLine($"缩略图    {PosterRunner.Summarize(posters)}");
                foreach (var line in PosterRunner.FailureLines(posters))
                {
                    Console.WriteLine($"  {line}");
                }
            }
        }

        if (report.Problems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"问题      {report.Problems.Count} 项");
            foreach (var p in report.Problems.Take(20))
            {
                var tag = p.Fatal ? "跳过" : "注意";
                Console.WriteLine($"  [{tag}] {p.RelPath}");
                Console.WriteLine($"         {p.Message}");
            }

            if (report.Problems.Count > 20)
            {
                Console.WriteLine($"  …… 另有 {report.Problems.Count - 20} 项未显示");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"耗时      {outcome.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static SourceLayout ParseLayout(string? value, string root) => value?.ToLowerInvariant() switch
    {
        null or "" or "auto" => LayoutDetector.Detect(root),
        "dual" => SourceLayout.Dual,
        "single" => SourceLayout.Single,
        _ => throw new CliException($"--layout 只接受 auto / dual / single，收到 {value}"),
    };

    private static VerifyMode ParseVerify(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => VerifyMode.Sample,
        "none" => VerifyMode.None,
        "sample" => VerifyMode.Sample,
        "full" => VerifyMode.Full,
        _ => throw new CliException($"--verify 只接受 none / sample / full，收到 {value}"),
    };

    private static string VerifyName(VerifyMode mode, SourceLayout layout)
    {
        if (layout == SourceLayout.Single) return "不适用（single 源无完整性契约）";

        return mode switch
        {
            VerifyMode.None => "none（仅存在性）",
            VerifyMode.Sample => "sample（存在性 + 字节数）",
            _ => "full（存在性 + 字节数 + md5）",
        };
    }

    private static string LayoutName(SourceLayout layout) =>
        layout == SourceLayout.Dual ? "dual" : "single";

    private static int ParsePositive(string? value, int fallback) =>
        int.TryParse(value, out var n) && n > 0 ? n : fallback;

    private static int? ParseOptionalPositive(string? value) =>
        int.TryParse(value, out var n) && n > 0 ? n : null;
}
