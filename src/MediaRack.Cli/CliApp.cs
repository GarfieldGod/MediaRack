using MediaRack.Store;

namespace MediaRack.Cli;

internal static class CliApp
{
    /// <summary>
    /// 未指定 --meta 时的默认元数据目录。
    /// <para>
    /// 必须与 <c>ServerOptions.DefaultMetaRoot</c> 指向同一处，否则「CLI 扫进 A 目录、服务读 B 目录」
    /// 会表现为"库是空的"。取 Local 而非 Roaming：库是几十 GB 的本地数据，
    /// 不该被域环境的漫游配置同步搬来搬去。
    /// </para>
    /// </summary>
    internal static string DefaultMetaRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRack");

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        var verb = args[0];
        var rest = args.Skip(1).ToArray();
        var meta = ResolveMetaTarget(Options.Parse(rest));

        try
        {
            return verb switch
            {
                "db" => DbCommand.Run(rest),
                "scan" => ScanCommand.Run(rest, meta),
                "posters" => PosterCommand.Run(rest, meta),
                "ls" => LsCommand.Run(rest, meta),
                "meta" => MetaCommand.Run(rest, meta),
                "collect" => CollectCommand.Run(rest, meta),
                "source" => SourceCommand.Run(rest, meta),
                "verify-readonly" => VerifyReadonlyCommand.Run(rest, meta),
                "doctor" => DoctorCommand.Run(rest, meta),
                "version" or "--version" or "-v" => PrintVersion(),
                "help" or "--help" or "-h" => PrintHelp(rest),
                _ => Unknown(verb),
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"找不到文件：{ex.FileName ?? ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>解析 --meta；显式给了却没值视为参数错误，而不是悄悄退回默认目录。</summary>
    internal static string ResolveMetaTarget(Options options)
    {
        if (!options.Flag("meta")) return DefaultMetaRoot;

        var value = options.Get("meta");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliException("--meta 需要一个目录或 .db 文件路径");
        }

        return value;
    }

    private static int PrintVersion()
    {
        var v = typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Console.WriteLine($"mrack {v}  (schema v{LibraryDatabase.SupportedVersion})");
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"未知命令：{verb}");
        Console.Error.WriteLine();
        PrintHelp();
        return 2;
    }

    private static int PrintHelp(IReadOnlyList<string>? scope = null)
    {
        if (scope is { Count: > 0 })
        {
            switch (scope[0])
            {
                case "db":
                    Console.WriteLine(DbCommand.HelpText);
                    return 0;
                case "scan":
                    Console.WriteLine(ScanCommand.HelpText);
                    return 0;
                case "posters":
                    Console.WriteLine(PosterCommand.HelpText);
                    return 0;
                case "ls":
                    Console.WriteLine(LsCommand.HelpText);
                    return 0;
                case "meta":
                    Console.WriteLine(MetaCommand.HelpText);
                    return 0;
                case "collect":
                    Console.WriteLine(CollectCommand.HelpText);
                    return 0;
                case "source":
                    Console.WriteLine(SourceCommand.HelpText);
                    return 0;
                case "verify-readonly":
                    Console.WriteLine(VerifyReadonlyCommand.HelpText);
                    return 0;
                case "doctor":
                    Console.WriteLine(DoctorCommand.HelpText);
                    return 0;
            }
        }

        Console.WriteLine(
            """
            mrack — MediaRack 命令行（多源视频库）

            用法：mrack <命令> [选项]

            命令：
              scan               扫描一个源目录（dual：B 站缓存镜像 / single：普通视频）
              posters            为缺封面的条目补缩略图
              ls                 列出库里的条目
              meta               查看 / 修改虚拟元数据（改名、标签、隐藏、旋转、进度）
              collect            手工收藏夹（多对多，与物理目录无关）
              source             源清单与源级设置（虚拟写权限）
              verify-readonly    证明源目录只读：操作前后全量快照 diff 必须为空
              doctor             环境自检（ffmpeg、库、源）
              db init            创建或升级元数据库
              db info            查看库状态（不写入）
              version            显示版本
              help               显示本帮助

            通用选项：
              --meta <目录>       元数据目录（默认 %APPDATA%\MediaRack）
              --meta <文件.db>    直接指定库文件

            示例：
              mrack scan --root <视频目录> --source bili
              mrack scan --root <视频目录> --layout single --verify none
              mrack verify-readonly --source bili --hash
              mrack meta title <key> "我的新标题"
              mrack collect add 收藏 <key>
              mrack ls --limit 20
            """);
        return 0;
    }
}
