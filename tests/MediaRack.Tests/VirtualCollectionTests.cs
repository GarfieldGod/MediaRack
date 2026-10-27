using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Tests;

/// <summary>
/// 收藏（用户层）：多对多、跨源、可改名/删除；与结构合集互不影响。
/// <para>
/// v4 起权限闸门在端点层（服务 writable / 源可见性），仓储只管数据——
/// 所以这里验证的是"加/删/幂等/改名/删除/整体替换"，不再验虚拟写权限。
/// </para>
/// </summary>
public sealed class VirtualCollectionTests : IDisposable
{
    private readonly string _metaDir = Path.Combine(
        Path.GetTempPath(), "mrack-vcol", Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDatabase _db;
    private readonly FavoriteRepository _repo;
    private readonly CollectionRepository _collections;
    private readonly SourceRepository _sources;

    public VirtualCollectionTests()
    {
        Directory.CreateDirectory(_metaDir);
        _db = LibraryDatabase.Open(_metaDir);
        _db.Migrate();
        _repo = new FavoriteRepository(_db);
        _collections = new CollectionRepository(_db);
        _sources = new SourceRepository(_db);

        _sources.Upsert(Source(VirtualWrite.Full));
        new MediaRepository(_db).Upsert("s", [Entry("A", "a"), Entry("B", "b")]);
        new CollectionRebuilder(_db).Rebuild();
    }

    private static SourceConfig Source(VirtualWrite level) => new()
    {
        Id = "s",
        Name = "库",
        Layout = SourceLayout.Single,
        Root = Path.Combine(Path.GetTempPath(), "mrack-vcol-root"),
        VirtualWrite = level,
    };

    private static MediaEntry Entry(string? relDir, string name) => new()
    {
        SourceId = "s",
        Key = MediaKey.ForSingle(relDir is null ? $"{name}.mp4" : $"{relDir}/{name}.mp4"),
        RelPath = relDir is null ? $"{name}.mp4" : $"{relDir}/{name}.mp4",
        RelDir = relDir,
        Layout = SourceLayout.Single,
        TitleRaw = name,
    };

    private static FavoriteRepository.ItemRef Ref(string relPath) =>
        new("s", MediaKey.ForSingle(relPath));

    [Fact]
    public void Create_UsesFavPrefix_AndIsFindableByName()
    {
        var id = _repo.Create("精选");

        Assert.StartsWith("fav:", id);
        Assert.Equal("精选", _repo.FindByName("精选")!.Name);
        // 与结构合集的 id 空间不重叠
        Assert.DoesNotContain(id, _collections.ListAll().Select(c => c.Id));
    }

    [Fact]
    public void Create_DuplicateName_Rejected()
    {
        _repo.Create("精选");

        Assert.Throws<InvalidOperationException>(() => _repo.Create(" 精选 "));
    }

    [Fact]
    public void FindByName_IgnoresCase()
    {
        _repo.Create("Favorites");

        Assert.NotNull(_repo.FindByName("favorites"));
    }

    [Fact]
    public void EnsureDefault_CreatesOnceAndReuses()
    {
        var first = _repo.EnsureDefault();
        var second = _repo.EnsureDefault();

        Assert.Equal(first, second);
        Assert.Equal(FavoriteRepository.DefaultId, first);
        Assert.Single(_repo.List());
    }

    [Fact]
    public void Rebuild_DoesNotTouchFavorites()
    {
        var id = _repo.Create("精选");
        _repo.AddItems(id, [Ref("A/a.mp4")]);

        // 重扫 → 合集重建，收藏必须原封不动
        new CollectionRebuilder(_db).Rebuild();

        Assert.Single(_repo.List());
        Assert.Single(_repo.ListItems(id));
    }

    [Fact]
    public void AddItems_WorksAndCounts()
    {
        var id = _repo.Create("精选");

        var added = _repo.AddItems(id, [Ref("A/a.mp4"), Ref("B/b.mp4")]);

        Assert.Equal(2, added);
        Assert.Equal(2, _repo.ListItems(id).Count);
    }

    [Fact]
    public void AddItems_Twice_IsIdempotent()
    {
        var id = _repo.Create("精选");
        var items = new[] { Ref("A/a.mp4") };

        _repo.AddItems(id, items);
        var second = _repo.AddItems(id, items);

        Assert.Equal(0, second);
        Assert.Single(_repo.ListItems(id));
    }

    [Fact]
    public void RemoveItems_RemovesOnlyThatFavorite()
    {
        var one = _repo.Create("一");
        var two = _repo.Create("二");
        var items = new[] { Ref("A/a.mp4") };
        _repo.AddItems(one, items);
        _repo.AddItems(two, items);

        Assert.Equal(1, _repo.RemoveItems(one, items));

        Assert.Empty(_repo.ListItems(one));
        Assert.Single(_repo.ListItems(two)); // 多对多：另一个夹子不受影响
    }

    [Fact]
    public void ReplaceMembership_SetsExactSet()
    {
        var one = _repo.Create("一");
        var two = _repo.Create("二");

        _repo.ReplaceMembership("s", MediaKey.ForSingle("A/a.mp4"), [one, two]);
        Assert.Equal(2, _repo.FavoritesOf("s", MediaKey.ForSingle("A/a.mp4")).Count);

        // 整体替换：只留 two
        _repo.ReplaceMembership("s", MediaKey.ForSingle("A/a.mp4"), [two]);
        Assert.Equal([two], _repo.FavoritesOf("s", MediaKey.ForSingle("A/a.mp4")));

        // 空 = 从所有收藏夹移除
        _repo.ReplaceMembership("s", MediaKey.ForSingle("A/a.mp4"), []);
        Assert.Empty(_repo.FavoritesOf("s", MediaKey.ForSingle("A/a.mp4")));
    }

    [Fact]
    public void Rename_KeepsItems()
    {
        var id = _repo.Create("旧名");
        _repo.AddItems(id, [Ref("A/a.mp4")]);

        _repo.Rename(id, "新名");

        Assert.Equal("新名", _repo.FindByName("新名")!.Name);
        Assert.Single(_repo.ListItems(id));
    }

    [Fact]
    public void Delete_RemovesItemsAndCollections_LeavesStructure()
    {
        var id = _repo.Create("精选");
        _repo.AddItems(id, [Ref("A/a.mp4")]);
        _repo.AddCollections(id, [_collections.ListBySource("s").First().Id]);

        Assert.True(_repo.Delete(id));

        Assert.Empty(_repo.List());
        Assert.Empty(_repo.ListItems(id));
        Assert.Empty(_repo.ListCollections(id));

        // 结构合集不受收藏删除影响
        Assert.Equal(2, _collections.ListBySource("s").Count);
    }

    [Fact]
    public void FavoriteCollections_AddAndRemove()
    {
        var id = _repo.Create("关注");
        var upIds = _collections.ListBySource("s").Select(c => c.Id).ToList();

        Assert.Equal(2, _repo.AddCollections(id, upIds));
        Assert.Equal(2, _repo.ListCollections(id).Count);
        Assert.Equal(1, _repo.RemoveCollections(id, [upIds[0]]));
        Assert.Equal([upIds[1]], _repo.ListCollections(id));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_metaDir)) Directory.Delete(_metaDir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }
}
