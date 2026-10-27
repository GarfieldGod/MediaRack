using MediaRack.Store;

namespace MediaRack.Cli;

internal static class LsCommand
{
    public const string HelpText =
        """
        mrack ls — 列出库里的条目

        用法：
          mrack ls [--source <id>] [--limit <n>]

        选项：
          --source <id>      只看某个源
          --limit <n>        最多显示几条（默认 50）
          --all              连隐藏条目一起显示（默认不显示）
          --meta <目录|db>   元数据目录

        标记：
          标题后的 ✎ 表示该条目的展示标题被虚拟覆盖过（原名仍在库里）
        """;

    public static int Run(string[] args, string metaTarget)
    {
        var options = Options.Parse(args);
        if (options.Flag("help"))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var limit = int.TryParse(options.Get("limit"), out var n) && n > 0 ? n : 50;

        using var db = LibraryDatabase.Open(metaTarget, create: false);
        var rows = new MediaRepository(db).List(options.Get("source"), limit, options.Flag("all"));

        if (rows.Count == 0)
        {
            Console.WriteLine("（无条目）");
            return 0;
        }

        // 中文标题在等宽字体下宽度不定，这里按字符数近似对齐即可，不追求像素级。
        Console.WriteLine($"{"源",-10} {"状态",-8} {"分辨率",-11} {"时长",-9} {"图",-2} {"目录",-14} 标题");
        Console.WriteLine(new string('-', 100));

        foreach (var r in rows)
        {
            var size = r.Width is { } w && r.Height is { } h ? $"{w}x{h}" : "-";
            var duration = r.DurationMs is { } ms ? $"{ms / 1000.0:F1}s" : "-";
            var poster = string.IsNullOrEmpty(r.PosterRel) ? "-" : "✓";
            var dir = r.RelDir ?? "（根）";
            var title = (r.Hidden ? "[隐藏] " : string.Empty) + r.Title + (r.TitleOverridden ? "  ✎" : string.Empty);

            Console.WriteLine(
                $"{Trunc(r.SourceId, 10),-10} {r.Status,-8} {size,-11} {duration,-9} {poster,-2} {Trunc(dir, 14),-14} {title}");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {rows.Count} 条" + (rows.Count == limit ? "（已达上限，用 --limit 调整）" : string.Empty));
        return 0;
    }

    /// <summary>按字符数截断（不对齐东亚宽字符，够用即可）。</summary>
    private static string Trunc(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
