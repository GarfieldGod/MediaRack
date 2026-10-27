using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Cli;

internal static class PosterCommand
{
    public const string HelpText =
        """
        mrack posters — 为库里缺封面的条目补缩略图

        用法：
          mrack posters [--source <id>] [选项]

        选项：
          --source <id>      只处理某个源；省略则处理全部源
          --limit <n>        本轮最多生成几张（分批跑，避免一次跑几小时）
          --width <n>        缩略图最大宽度（默认 560，不放大）
          --force            已有缩略图也重新生成
          --meta <目录|db>   元数据目录

        说明：
          抽帧取视频中点帧，竖屏保持原始比例（不裁 16:9）。
          只写 meta/poster/，源目录永不修改。
        """;

    public static int Run(string[] args, string metaTarget)
    {
        var options = Options.Parse(args);
        if (options.Flag("help"))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        // 查询类操作不该顺手建出空库。
        using var db = LibraryDatabase.Open(metaTarget, create: false);

        var sourceRepo = new SourceRepository(db);
        var sources = sourceRepo.List();
        if (sources.Count == 0)
        {
            Console.Error.WriteLine("库里还没有任何源，先跑 mrack scan。");
            return 1;
        }

        var filter = options.Get("source");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            sources = sources.Where(s => string.Equals(s.Id, filter, StringComparison.Ordinal)).ToList();
            if (sources.Count == 0) throw new CliException($"库里没有源 {filter}");
        }

        var posterOptions = new PosterOptions(
            MaxWidth: ParsePositive(options.Get("width"), PosterMaker.DefaultMaxWidth),
            Limit: ParseOptionalPositive(options.Get("limit")),
            Force: options.Flag("force"));

        var totalGenerated = 0;
        var totalFailed = 0;

        foreach (var source in sources)
        {
            Console.WriteLine($"源        {source.Id}  ({source.Root})");

            var report = PosterRunner.Execute(db, source, metaTarget, posterOptions);
            if (report is null) return 1;

            Console.WriteLine($"  {PosterRunner.Summarize(report)}");
            foreach (var line in PosterRunner.FailureLines(report))
            {
                Console.WriteLine($"  {line}");
            }

            totalGenerated += report.Generated;
            totalFailed += report.Failed;
            Console.WriteLine();
        }

        Console.WriteLine($"合计      生成 {totalGenerated}，失败 {totalFailed}");
        return 0;
    }

    private static int ParsePositive(string? value, int fallback) =>
        int.TryParse(value, out var n) && n > 0 ? n : fallback;

    private static int? ParseOptionalPositive(string? value) =>
        int.TryParse(value, out var n) && n > 0 ? n : null;
}
