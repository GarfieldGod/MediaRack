using MediaRack.Library;
using MediaRack.Models;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Tests;

/// <summary>
/// 合集归属 override（v7）：手动移动到合集后，重建（扫描）不再打回；
/// 目标合集不存在时回退投影；手动合集（custom）从引用按需建行。
/// 顺带覆盖 FingerprintSync 的收藏搬家（认领 + missing 释放）。
/// </summary>
public sealed class CollectionOverrideTests : IDisposable
{
    private readonly string _metaDir = Path.Combine(
        Path.GetTempPath(), "mrack-colv", Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-colv-root", Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDatabase _db;

    public CollectionOverrideTests()
    {
        Directory.CreateDirectory(_metaDir);
        Directory.CreateDirectory(_root);
        _db = LibraryDatabase.Open(_metaDir);
        _db.Migrate();

        new SourceRepository(_db).Upsert(new SourceConfig
        {
            Id = "s",
            Name = "库",
            Layout = SourceLayout.Single,
            Root = _root,
            VirtualWrite = VirtualWrite.Full,
        });
        // 两个条目：一个在子目录"电影"（投影合集 dir:s:/电影），一个在根
        new MediaRepository(_db).Upsert("s",
        [
            Entry("电影", "a"),
            Entry(null, "b"),
        ]);
        new CollectionRebuilder(_db).Rebuild();
    }

    private MediaMeta Meta(string key) =>
        new MetaRepository(_db).Get("s", key) ?? new MediaMeta { SourceId = "s", Key = key };

    private string? BoundCollection(string key) =>
        new MediaRepository(_db).Get("s", key)?.CollectionId;

    private static MediaEntry Entry(string? relDir, string name) => new()
    {
        SourceId = "s",
        Key = MediaKey.ForSingle(relDir is null ? $"{name}.mp4" : $"{relDir}/{name}.mp4"),
        RelPath = relDir is null ? $"{name}.mp4" : $"{relDir}/{name}.mp4",
        RelDir = relDir,
        Layout = SourceLayout.Single,
        TitleRaw = name,
    };

    [Fact]
    public void Override_ToExistingFolderCollection_SurvivesRebuild()
    {
        // b 在根（dir:s:/），手动移进"电影"合集
        var meta = Meta(MediaKey.ForSingle("b.mp4")) with { };
        new MetaRepository(_db).Apply("s", MediaKey.ForSingle("b.mp4"),
            new MetaPatch { Collection = "dir:/电影" });

        new CollectionRebuilder(_db).Rebuild();

        Assert.Equal("dir:s:/电影", BoundCollection(MediaKey.ForSingle("b.mp4")));
    }

    [Fact]
    public void Override_ToMissingFolder_FallsBackToProjection()
    {
        new MetaRepository(_db).Apply("s", MediaKey.ForSingle("b.mp4"),
            new MetaPatch { Collection = "dir:/不存在的目录" });

        new CollectionRebuilder(_db).Rebuild();

        // 目标合集不存在 → 回退目录投影
        Assert.Equal("dir:s:/", BoundCollection(MediaKey.ForSingle("b.mp4")));
    }

    [Fact]
    public void Override_ToCustom_CreatesCollectionRow_AndBinds()
    {
        new MetaRepository(_db).Apply("s", MediaKey.ForSingle("b.mp4"),
            new MetaPatch { Collection = "custom:精选" });

        new CollectionRebuilder(_db).Rebuild();

        var bound = BoundCollection(MediaKey.ForSingle("b.mp4"));
        Assert.Equal("custom:s:精选", bound);
        // 合集行存在且 kind=custom
        var row = new CollectionRepository(_db).ListAll().Single(c => c.Id == bound);
        Assert.Equal("精选", row.Name);
        Assert.Equal("custom", row.Kind);
    }

    [Fact]
    public void ClearOverride_RestoresProjection()
    {
        var repo = new MetaRepository(_db);
        repo.Apply("s", MediaKey.ForSingle("b.mp4"),
            new MetaPatch { Collection = "custom:精选" });
        new CollectionRebuilder(_db).Rebuild();
        Assert.Equal("custom:s:精选", BoundCollection(MediaKey.ForSingle("b.mp4")));

        // 空串 = 清除 override → 恢复目录投影
        repo.Apply("s", MediaKey.ForSingle("b.mp4"), new MetaPatch { Collection = "" });
        new CollectionRebuilder(_db).Rebuild();

        Assert.Equal("dir:s:/", BoundCollection(MediaKey.ForSingle("b.mp4")));
    }

    [Fact]
    public void ApplyOverride_TakesEffectImmediately()
    {
        // 真实链路 = 接口先写 meta override，再让 Rebuilder 即时落位
        var key = MediaKey.ForSingle("b.mp4");
        new MetaRepository(_db).Apply("s", key, new MetaPatch { Collection = "custom:稍后看" });
        new CollectionRebuilder(_db).ApplyOverride("s", [key], "custom:稍后看");

        Assert.Equal("custom:s:稍后看", BoundCollection(key));
        // 重建后依然在（引用派生建行 + override 段回绑）
        new CollectionRebuilder(_db).Rebuild();
        Assert.Equal("custom:s:稍后看", BoundCollection(key));
    }

    [Fact]
    public void FingerprintSync_ClaimsFavorites_AndReleasesMissingOldKey()
    {
        var content = Path.Combine(_root, "旧", "v.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(content)!);
        File.WriteAllBytes(content, new byte[4096]);

        var oldEntry = new MediaEntry
        {
            SourceId = "s",
            Key = MediaKey.ForSingle("旧/v.mp4"),
            RelPath = "旧/v.mp4",
            RelDir = "旧",
            Layout = SourceLayout.Single,
            TitleRaw = "v",
            SizeBytes = 4096,
            Status = MediaStatus.Ok,
        };
        var newEntry = new MediaEntry
        {
            SourceId = "s",
            Key = MediaKey.ForSingle("新/v.mp4"),
            RelPath = "新/v.mp4",
            RelDir = "新",
            Layout = SourceLayout.Single,
            TitleRaw = "v",
            SizeBytes = 4096,
            Status = MediaStatus.Ok,
        };

        var media = new MediaRepository(_db);
        media.Upsert("s", [oldEntry]);

        // 旧位置：有 meta（旋转）且被收藏
        new MetaRepository(_db).Apply("s", oldEntry.Key,
            new MetaPatch { Rotation = 90 });
        var fav = new FavoriteRepository(_db);
        var favId = fav.Create("精选");
        fav.AddItems(favId, [new FavoriteRepository.ItemRef("s", oldEntry.Key)]);

        // 第一次 Sync：登记旧位置指纹
        FingerprintSync.Sync(_db, [oldEntry],
            new Dictionary<string, string> { [oldEntry.Key] = _root });

        // "移动"：新位置出现同内容文件，旧条目标 missing
        var newDir = Path.Combine(_root, "新");
        Directory.CreateDirectory(newDir);
        File.WriteAllBytes(Path.Combine(newDir, "v.mp4"), new byte[4096]);
        var missingOld = oldEntry;
        missingOld.Status = MediaStatus.Missing;
        media.Upsert("s", [missingOld, newEntry]);

        // 第二次 Sync：新位置认领
        FingerprintSync.Sync(_db, [newEntry],
            new Dictionary<string, string> { [newEntry.Key] = _root });

        // meta 认领：旋转跟过来了
        Assert.Equal(90, new MetaRepository(_db).Get("s", newEntry.Key)!.Rotation);
        // 收藏认领：新位置在夹子里
        Assert.Contains(new FavoriteRepository.ItemRef("s", newEntry.Key), fav.ListItems(favId));
        // 收藏释放：旧条目已 missing，收藏不再挂在它名下
        Assert.DoesNotContain(new FavoriteRepository.ItemRef("s", oldEntry.Key), fav.ListItems(favId));
    }

    public void Dispose()
    {
        try
        {
            _db.Dispose();
            Directory.Delete(_metaDir, recursive: true);
            Directory.Delete(Path.Combine(Path.GetTempPath(), "mrack-colv-root"), recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
