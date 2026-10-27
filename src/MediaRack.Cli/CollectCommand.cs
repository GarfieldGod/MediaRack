using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Cli;

/// <summary>
/// <c>mrack collect</c> — 手工收藏夹。
/// <para>
/// 与扫描时自动生成的"目录合集"完全正交：自动合集每次扫描全量重算，
/// 手工收藏夹只由用户改，扫描永远碰不到它（两者靠 id 前缀区分）。
/// </para>
/// </summary>
internal static class CollectCommand
{
    public const string HelpText =
        """
        mrack collect — 手工收藏夹（只写库，磁盘永不变）

        用法：
          mrack collect ls
          mrack collect show   <名称>
          mrack collect new    <名称>
          mrack collect rename <名称> <新名称>
          mrack collect rm     <名称>
          mrack collect add    <名称> <key> [<key>...] [--source <id>]
          mrack collect remove <名称> <key> [<key>...]

        选项：
          --source <id>   多个源含同一个 key 时必须指定

        说明：
          一个条目可以同时属于任意多个收藏夹（多对多），与它所在的物理目录无关。
          加入收藏夹要求该源的虚拟写权限不是 none；移除不受限制。
        """;

    public static int Run(string[] args, string metaTarget)
    {
        var options = Options.Parse(args);
        if (options.Flag("help") || options.Positional.Count == 0)
        {
            Console.WriteLine(HelpText);
            return options.Flag("help") ? 0 : 2;
        }

        var sub = options.Positional[0];
        var name = options.Positional.Count > 1 ? options.Positional[1] : string.Empty;
        var keys = options.Positional.Skip(2).ToList();

        using var db = LibraryDatabase.Open(metaTarget, create: false);
        var repo = new FavoriteRepository(db);

        switch (sub)
        {
            case "ls":
                return List(repo);

            case "new":
            {
                var id = repo.Create(name);
                var row = repo.Get(id)!;
                Console.WriteLine($"已创建    {row.Name}    （id {id}）");
                return 0;
            }

            case "rename":
            {
                if (keys.Count == 0) throw new CliException("缺少新名称");
                var target = Require(repo, name);
                repo.Rename(target.Id, keys[0]);
                Console.WriteLine($"已重命名  {target.Name} → {keys[0].Trim()}");
                return 0;
            }

            case "rm":
            {
                var target = Require(repo, name);
                repo.Delete(target.Id);
                Console.WriteLine($"已删除    {target.Name}（{target.ItemCount} 条关联）");
                return 0;
            }

            case "show":
                return Show(repo, Require(repo, name));

            case "add":
            {
                if (keys.Count == 0) throw new CliException("缺少要加入的 key");
                var target = Require(repo, name);
                var items = Resolve(db, options.Get("source"), keys);

                var changed = repo.AddItems(target.Id, items);
                Console.WriteLine($"已加入    {changed} 条 → {target.Name}");
                return 0;
            }

            case "remove":
            {
                if (keys.Count == 0) throw new CliException("缺少要移除的 key");
                var target = Require(repo, name);
                var items = Resolve(db, options.Get("source"), keys);

                var changed = repo.RemoveItems(target.Id, items);
                Console.WriteLine($"已移除    {changed} 条 ← {target.Name}");
                return 0;
            }

            default:
                throw new CliException($"未知子命令：collect {sub}");
        }
    }

    private static int List(FavoriteRepository repo)
    {
        var favorites = repo.List();
        if (favorites.Count == 0)
        {
            Console.WriteLine("（还没有收藏夹，用 mrack collect new <名称> 建一个）");
            return 0;
        }

        foreach (var c in favorites)
        {
            Console.WriteLine($"{c.ItemCount,5} 条 / {c.CollectionCount,3} 个合集  {c.Name}");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {favorites.Count} 个收藏夹");
        return 0;
    }

    private static int Show(FavoriteRepository repo, FavoriteRepository.FavoriteRow favorite)
    {
        var keys = repo.ListItems(favorite.Id);
        var collections = repo.ListCollections(favorite.Id);

        Console.WriteLine($"收藏夹    {favorite.Name}");
        Console.WriteLine($"id        {favorite.Id}");
        Console.WriteLine();

        if (keys.Count == 0)
        {
            Console.WriteLine("（空的）");
        }
        else
        {
            Console.WriteLine($"{"源",-12} {"key",-24}");
            Console.WriteLine(new string('-', 60));
            foreach (var item in keys)
            {
                Console.WriteLine($"{item.SourceId,-12} {item.Key,-24}");
            }

            Console.WriteLine();
            Console.WriteLine($"共 {keys.Count} 条");
        }

        if (collections.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("收藏的合集：");
            foreach (var cid in collections) Console.WriteLine($"  {cid}");
        }

        return 0;
    }

    /// <summary>按名称取收藏夹；重名不可能出现（创建时就拒绝），所以这里只处理"不存在"。</summary>
    private static FavoriteRepository.FavoriteRow Require(FavoriteRepository repo, string name) =>
        repo.FindByName(name) ?? throw new CliException($"收藏夹不存在：{name}");

    private static IReadOnlyList<FavoriteRepository.ItemRef> Resolve(
        LibraryDatabase db, string? sourceId, IReadOnlyList<string> keys)
    {
        var list = new List<FavoriteRepository.ItemRef>(keys.Count);
        foreach (var key in keys)
        {
            var (sid, k) = TargetResolver.Resolve(db, sourceId, key);
            list.Add(new FavoriteRepository.ItemRef(sid, k));
        }

        return list;
    }
}
