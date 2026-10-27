using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Tests;

/// <summary>
/// v4 合集模型：single 按目录平铺、dual 按 UP 主平铺，一个视频恰好属于一个合集。
/// 合集是 media 的投影——重建幂等，磁盘一行都不碰。
/// </summary>
public sealed class CollectionRepositoryTests : IDisposable
{
    private readonly string _metaDir = Path.Combine(
        Path.GetTempPath(), "mrack-col", Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDatabase _db;
    private readonly CollectionRepository _repo;
    private readonly MediaRepository _media;

    public CollectionRepositoryTests()
    {
        Directory.CreateDirectory(_metaDir);
        _db = LibraryDatabase.Open(_metaDir);
        _db.Migrate();
        _repo = new CollectionRepository(_db);
        _media = new MediaRepository(_db);

        new SourceRepository(_db).Upsert(new SourceConfig
        {
            Id = "s",
            Name = "我的视频",
            Layout = SourceLayout.Single,
            Root = Path.Combine(_metaDir, "root"),
        });
    }

    private static MediaEntry SingleEntry(string? relDir, string name, string sourceId = "s")
    {
        var relPath = relDir is null ? $"{name}.mp4" : $"{relDir}/{name}.mp4";
        return new MediaEntry
        {
            SourceId = sourceId,
            Key = MediaKey.ForSingle(relPath),
            RelPath = relPath,
            RelDir = relDir,
            Layout = SourceLayout.Single,
            TitleRaw = name,
            Status = MediaStatus.Ok,
        };
    }

    private static MediaEntry DualEntry(string avid, string cid, string? upMid, string? upName,
        string relDir = "收藏夹", string sourceId = "bili")
    {
        return new MediaEntry
        {
            SourceId = sourceId,
            Key = MediaKey.ForDual(avid, cid),
            RelPath = $"{relDir}/{avid}/c_{cid}",
            RelDir = relDir,
            Layout = SourceLayout.Dual,
            TitleRaw = $"视频 {avid}",
            UpMid = upMid,
            UpName = upName,
            Status = MediaStatus.Ok,
        };
    }

    private CollectionRebuilder.Result Rebuild() => new CollectionRebuilder(_db).Rebuild();

    private IReadOnlyList<CollectionRow> Of(string sourceId) => _repo.ListBySource(sourceId);

    [Fact]
    public void Single_OneCollectionPerDirectoryWithVideos()
    {
        _media.Upsert("s", [SingleEntry("合集 一", "a"), SingleEntry("合集 一", "b"),
            SingleEntry("合集 二", "c"), SingleEntry(null, "散落")]);
        var result = Rebuild();

        var rows = Of("s");
        Assert.Equal(3, rows.Count); // 两个目录 + 根
        Assert.Equal(3, result.Collections);

        Assert.Equal(2, rows.Single(r => r.Path == "合集 一").ItemCount);
        Assert.Equal(1, rows.Single(r => r.Path == "合集 二").ItemCount);

        // 根合集用源名，子目录用末段
        var root = rows.Single(r => r.Path == "");
        Assert.Equal("我的视频", root.Name);
        Assert.Equal(CollectionRebuilder.RootFolderId("s"), root.Id);
        Assert.Equal("合集 一", rows.Single(r => r.Path == "合集 一").Name);
        Assert.Equal("folder", root.Kind);
    }

    [Fact]
    public void Single_NestedDirectory_IsFlatAndAncestorIsNotACollection()
    {
        // 科幻 本身没有视频 → 不成合集；科幻/2024 自成合集，名字是末段 "2024"
        _media.Upsert("s", [SingleEntry("科幻/2024", "ep1")]);
        Rebuild();

        var rows = Of("s");
        var only = Assert.Single(rows);
        Assert.Equal("2024", only.Name);
        Assert.Equal("科幻/2024", only.Path);
        Assert.DoesNotContain(rows, r => r.Path == "科幻");
    }

    [Fact]
    public void Single_EachItemBelongsToExactlyOneCollection()
    {
        _media.Upsert("s", [SingleEntry("A", "a"), SingleEntry("B", "b"), SingleEntry(null, "loose")]);
        Rebuild();

        var keys = new[]
        {
            MediaKey.ForSingle("A/a.mp4"),
            MediaKey.ForSingle("B/b.mp4"),
            MediaKey.ForSingle("loose.mp4"),
        };

        // 每个条目都有归属，且归属 id 就是它所在目录 / 根
        Assert.Equal(CollectionRebuilder.FolderId("s", "A"), _media.Get("s", keys[0])!.CollectionId);
        Assert.Equal(CollectionRebuilder.FolderId("s", "B"), _media.Get("s", keys[1])!.CollectionId);
        Assert.Equal(CollectionRebuilder.RootFolderId("s"), _media.Get("s", keys[2])!.CollectionId);

        // 排他：各合集条目互不相交，并集 = 全部条目
        var perCollection = Of("s").Select(r => _repo.ListItemKeys(r.Id).ToHashSet()).ToList();
        Assert.Equal(keys.Length, perCollection.Sum(set => set.Count));
        Assert.Equal(keys.OrderBy(k => k), perCollection.SelectMany(set => set).OrderBy(k => k));
    }

    [Fact]
    public void Dual_GroupsByUpMid_RegardlessOfDirectory()
    {
        new SourceRepository(_db).Upsert(new SourceConfig
        {
            Id = "bili", Name = "B站缓存", Layout = SourceLayout.Dual,
            Root = Path.Combine(_metaDir, "bili"),
        });

        _media.Upsert("bili", [
            DualEntry("1", "11", "100", "阿婆主A", relDir: "默认收藏夹"),
            DualEntry("2", "22", "100", "阿婆主A", relDir: "动画"),
            DualEntry("3", "33", "200", "阿婆主B"),
            DualEntry("4", "44", null, null),
        ]);
        Rebuild();

        var rows = Of("bili");
        Assert.Equal(3, rows.Count);

        var a = rows.Single(r => r.Id == CollectionRebuilder.UpId("bili", "100"));
        Assert.Equal("阿婆主A", a.Name);
        Assert.Equal("up", a.Kind);
        Assert.Equal(2, a.ItemCount); // 跨两个缓存目录仍是一个合集

        Assert.Equal(1, rows.Single(r => r.Kind == "up" && r.Path == "200").ItemCount);

        var unknown = rows.Single(r => r.Path == CollectionRebuilder.UnknownUp);
        Assert.Equal("未识别 UP 主", unknown.Name);
    }

    [Fact]
    public void Rebuild_IsIdempotent()
    {
        _media.Upsert("s", [SingleEntry("A", "a"), SingleEntry("B", "b")]);

        var first = Rebuild();
        var second = Rebuild();

        Assert.Equal(first.Collections, second.Collections);
        Assert.Equal(2, Of("s").Count);                                  // A、B
        Assert.DoesNotContain(Of("s"), r => r.Path == "");               // 根没有散落视频 → 不成合集
        Assert.Equal(2, Of("s").Sum(r => r.ItemCount));
    }

    [Fact]
    public void Rebuild_MissingItemsKeepTheirCollection()
    {
        _media.Upsert("s", [SingleEntry("A", "a"), SingleEntry("B", "b")]);
        Rebuild();
        Assert.Equal(2, Of("s").Count);

        // 目录被拔掉：条目先被标 missing（不删行），合集因此仍在——
        // 客户端看到"这个合集的内容不见了"，而不是整个合集凭空消失。
        _media.MarkMissingExcept("s", [MediaKey.ForSingle("A/a.mp4")]);
        Rebuild();

        Assert.Equal(2, Of("s").Count);
        Assert.Contains(Of("s"), r => r.Path == "B");
    }

    [Fact]
    public void Rebuild_DoesNotLeakAcrossSources()
    {
        new SourceRepository(_db).Upsert(new SourceConfig
        {
            Id = "t", Name = "另一个库", Layout = SourceLayout.Single,
            Root = Path.Combine(_metaDir, "root2"),
        });

        _media.Upsert("s", [SingleEntry("A", "a")]);
        Rebuild();

        Assert.Single(Of("s"));         // 只有 A（根没有散落视频）
        Assert.Empty(Of("t"));          // t 没有条目 → 没有合集
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
