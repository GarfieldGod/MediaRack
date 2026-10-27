using System.Diagnostics;
using MediaRack.Store;

namespace MediaRack.Cli;

internal static class DbCommand
{
    public const string HelpText =
        """
        mrack db — 元数据库

        用法：
          mrack db init [--meta <目录|文件.db>]     创建或升级库（幂等）
          mrack db info [--meta <目录|文件.db>]     查看状态（不写入，库不存在则报错）
        """;

    public static int Run(string[] args)
    {
        var sub = args.Length > 0 ? args[0] : "help";
        if (sub is "help" or "--help" or "-h")
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var options = Options.Parse(args.Skip(1).ToArray());
        var target = CliApp.ResolveMetaTarget(options);

        return sub switch
        {
            "init" => Init(target),
            "info" => Info(target),
            _ => Unknown(sub),
        };
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"未知子命令：db {sub}");
        Console.Error.WriteLine();
        Console.WriteLine(HelpText);
        return 2;
    }

    private static int Init(string target)
    {
        var sw = Stopwatch.StartNew();
        using var db = LibraryDatabase.Open(target);
        var result = db.Migrate();
        sw.Stop();

        Console.WriteLine($"库文件  {db.FilePath}");

        if (result.Changed)
        {
            var applied = string.Join(", ", result.Applied.Select(v => $"v{v}"));
            var from = result.From == 0 ? "空库" : $"v{result.From}";
            Console.WriteLine($"版本    {from} → v{result.To}（应用 {applied}）");
        }
        else
        {
            Console.WriteLine($"版本    v{result.To}（已是最新）");
        }

        Console.WriteLine($"表      {db.TableNames().Count} 张");
        Console.WriteLine($"用时    {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    private static int Info(string target)
    {
        using var db = LibraryDatabase.Open(target, create: false);
        var version = db.Version;
        var tables = db.TableNames();

        Console.WriteLine($"库文件  {db.FilePath}");
        Console.WriteLine($"schema  v{version}（程序支持 v{LibraryDatabase.SupportedVersion}）");
        Console.WriteLine($"表      {tables.Count} 张");
        Console.WriteLine($"        {string.Join(", ", tables)}");
        Console.WriteLine($"source  {db.CountRows("source")} 个");
        Console.WriteLine($"media   {db.CountMedia()} 条");
        Console.WriteLine($"meta    {db.CountRows("media_meta")} 条");
        Console.WriteLine($"collect {db.CountRows("collection")} 个收藏夹");
        return 0;
    }
}
