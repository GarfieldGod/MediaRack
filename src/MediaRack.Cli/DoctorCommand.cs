using MediaRack.Media;
using MediaRack.Store;

namespace MediaRack.Cli;

/// <summary>
/// 环境自检。写库之前先跑一次，能省掉"扫了半天才发现 ffmpeg 没装"。
/// </summary>
internal static class DoctorCommand
{
    public const string HelpText =
        """
        mrack doctor — 环境自检

        用法：
          mrack doctor [--meta <目录|db>]

        检查项：
          ffmpeg / ffprobe  是否可用及版本
          元数据库          路径、schema 版本、条目数
          ffmpeg 路径覆盖     环境变量 MEDIARACK_FFMPEG / MEDIARACK_FFPROBE
        """;

    public static int Run(string[] args, string metaTarget)
    {
        var options = Options.Parse(args);
        if (options.Flag("help"))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var ok = true;

        var tools = FfTools.Locate();
        var availability = tools.Check();

        Console.WriteLine("ffmpeg");
        Console.WriteLine($"  ffmpeg   {availability.FfmpegVersion ?? "(不可用)"}");
        Console.WriteLine($"           路径 {tools.Ffmpeg}");
        Console.WriteLine($"  ffprobe  {availability.FfprobeVersion ?? "(不可用)"}");
        Console.WriteLine($"           路径 {tools.Ffprobe}");

        if (!availability.Available)
        {
            ok = false;
            Console.WriteLine($"  提示     {availability.Error}");
        }

        Console.WriteLine();
        Console.WriteLine("元数据库");

        var dbPath = LibraryDatabase.ResolvePath(metaTarget);
        Console.WriteLine($"  路径     {dbPath}");

        if (!File.Exists(dbPath))
        {
            Console.WriteLine("  状态     尚未创建（跑一次 mrack scan 或 mrack db init）");
            return ok ? 0 : 1;
        }

        using var db = LibraryDatabase.Open(metaTarget, create: false);
        Console.WriteLine($"  版本     v{db.Version}（程序支持 v{LibraryDatabase.SupportedVersion}）");
        Console.WriteLine($"  条目     {db.CountMedia()} 条");

        foreach (var source in new SourceRepository(db).List())
        {
            Console.WriteLine(
                $"  源       {source.Id}  [{LayoutName(source.Layout)}]  " +
                $"写权限 {Virtual.MetaPolicy.Name(source.VirtualWrite)}  " +
                $"{db.CountMedia(source.Id)} 条  {source.Root}");
        }

        return ok ? 0 : 1;
    }

    private static string LayoutName(Models.SourceLayout layout) =>
        layout == Models.SourceLayout.Dual ? "dual" : "single";
}
