using MediaRack.Models;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Cli;

/// <summary>
/// <c>mrack source</c> — 源清单与源级设置。
/// <para>
/// 虚拟写权限是<b>源级</b>管理项，不属于条目写入，所以它自己不受该权限约束——
/// 否则一旦设成 <c>none</c> 就再也解不开了。
/// </para>
/// </summary>
internal static class SourceCommand
{
    public const string HelpText =
        """
        mrack source — 源清单与源级设置

        用法：
          mrack source ls
          mrack source vwrite <id> <none|whitelist|full>

        虚拟写权限（只影响服务端视野，磁盘永远不可写）：
          none       视图不可改，客户端只能存本地覆盖
          whitelist  只放行 标签 / 收藏夹 / 隐藏 / 旋转 / 进度，标题锁定
          full       全部虚拟字段可改
        """;

    public static int Run(string[] args, string metaTarget)
    {
        var options = Options.Parse(args);
        if (options.Flag("help") || options.Positional.Count == 0)
        {
            Console.WriteLine(HelpText);
            return options.Flag("help") ? 0 : 2;
        }

        using var db = LibraryDatabase.Open(metaTarget, create: false);
        var repo = new SourceRepository(db);

        switch (options.Positional[0])
        {
            case "ls":
                return List(repo);

            case "vwrite":
            {
                var id = options.Positional.Count > 1 ? options.Positional[1] : string.Empty;
                var raw = options.Positional.Count > 2 ? options.Positional[2] : string.Empty;

                var level = raw.ToLowerInvariant() switch
                {
                    "none" => VirtualWrite.None,
                    "whitelist" => VirtualWrite.Whitelist,
                    "full" => VirtualWrite.Full,
                    _ => throw new CliException($"权限只接受 none / whitelist / full，收到 {raw}"),
                };

                if (!repo.SetVirtualWrite(id, level))
                {
                    throw new CliException($"源不存在：{id}");
                }

                Console.WriteLine($"已设置    {id} 的虚拟写权限 = {MetaPolicy.Name(level)}（磁盘仍不可写）");
                return 0;
            }

            default:
                throw new CliException($"未知子命令：source {options.Positional[0]}");
        }
    }

    private static int List(SourceRepository repo)
    {
        var sources = repo.List();
        if (sources.Count == 0)
        {
            Console.WriteLine("（还没有源，用 mrack scan --root <目录> 添加）");
            return 0;
        }

        Console.WriteLine($"{"id",-14} {"布局",-7} {"写权限",-10} {"条目",-7} 目录");
        Console.WriteLine(new string('-', 100));
        foreach (var s in sources)
        {
            Console.WriteLine(
                $"{s.Id,-14} {(s.Layout == SourceLayout.Dual ? "dual" : "single"),-7} " +
                $"{MetaPolicy.Name(s.VirtualWrite),-10} {s.LastCount?.ToString() ?? "-",-7} {s.Root}");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {sources.Count} 个源");
        return 0;
    }
}
