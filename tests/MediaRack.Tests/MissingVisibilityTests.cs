using MediaRack.Library;
using MediaRack.Models;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Tests;

/// <summary>
/// "缺失条目"的显示口径与手动清理：
/// <list type="bullet">
/// <item>统计只算真实存在的条目（ok），缺失单独给数；</item>
/// <item>合集计数只算 ok，全缺失的合集在 UI 里不出现；</item>
/// <item>清理删掉全部位置记录，但档案（设置/收藏/指纹账）保留——
/// 搬回原路径靠 key 命中，搬到别处靠指纹认领。</item>
/// </list>
/// </summary>
public sealed class MissingVisibilityTests : IDisposable
{
    private readonly string _metaDir = Path.Combine(
        Path.GetTempPath(), "mrack-miss", Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-miss-root", Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDatabase _db;
    private readonly IReadOnlyList<MediaEntry> _entries;

    public MissingVisibilityTests()
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

        // 三条同目录条目：keep 有旋转档案、fav 有收藏、bare 什么都没有
        _entries =
        [
            Entry("片", "keep", 100),
            Entry("片", "fav", 200),
            Entry("片", "bare", 300),
        ];
        new MediaRepository(_db).Upsert("s", _entries);
        new CollectionRebuilder(_db).Rebuild();

        new MetaRepository(_db).Apply("s", Key("keep"), new MetaPatch { Rotation = 90 });
        var fav = new FavoriteRepository(_db);
        fav.AddItems(fav.EnsureDefault(), [new FavoriteRepository.ItemRef("s", Key("fav"))]);
    }

    private static string Key(string name) => MediaKey.ForSingle($"片/{name}.mp4");

    private static MediaEntry Entry(string relDir, string name, long size) => new()
    {
        SourceId = "s",
        Key = MediaKey.ForSingle($"{relDir}/{name}.mp4"),
        RelPath = $"{relDir}/{name}.mp4",
        RelDir = relDir,
        Layout = SourceLayout.Single,
        TitleRaw = name,
        SizeBytes = size,
    };

    /// <summary>模拟"整个文件夹搬出源根"之后的重扫结果：三条全部标 missing。</summary>
    private void MarkAllMissing()
    {
        new MediaRepository(_db).Upsert("s", _entries.Select(e => new MediaEntry
        {
            SourceId = e.SourceId,
            Key = e.Key,
            RelPath = e.RelPath,
            RelDir = e.RelDir,
            Layout = e.Layout,
            TitleRaw = e.TitleRaw,
            SizeBytes = e.SizeBytes,
            FileMtime = e.FileMtime,
            Status = MediaStatus.Missing,
        }).ToList());
        new CollectionRebuilder(_db).Rebuild();
    }

    [Fact]
    public void Counts_SeparateAvailableFromMissing()
    {
        Assert.Equal(3, _db.CountAvailable("s"));
        Assert.Equal(0, _db.CountMissing("s"));

        MarkAllMissing();

        // 搬走文件夹后：可用 0，缺失 3，含缺失的总行数仍是 3
        Assert.Equal(0, _db.CountAvailable("s"));
        Assert.Equal(3, _db.CountMissing("s"));
        Assert.Equal(3, _db.CountMedia("s"));
        // 体积只算 ok：缺失后归零
        Assert.Equal(0, _db.TotalMediaBytes("s"));
    }

    [Fact]
    public void CollectionItemCount_CountsOnlyAvailable()
    {
        MarkAllMissing();

        var coll = new CollectionRepository(_db).ListBySource("s")
            .Single(c => c.Kind == "folder" && c.Path == "片");

        // 合集行还在（投影不删），但计数是 0——UI 靠这个把空壳藏起来
        Assert.Equal(0, coll.ItemCount);
    }

    [Fact]
    public void Purge_DeletesEveryMissingRow_ButKeepsAnchors()
    {
        MarkAllMissing();

        var result = new MissingPurge(_db).Run("s");

        // 三条位置记录全删，不再按"有没有档案"区别对待
        Assert.Equal(3, result.Deleted);
        Assert.Equal(0, _db.CountMedia("s"));

        var repo = new MediaRepository(_db);
        Assert.Null(repo.Get("s", Key("keep")));
        Assert.Null(repo.Get("s", Key("fav")));
        Assert.Null(repo.Get("s", Key("bare")));

        // 档案原地不动：设置仍挂在原 key 上（搬回原路径即生效），收藏同理
        Assert.Equal(90, new MetaRepository(_db).Get("s", Key("keep"))!.Rotation);
        Assert.Contains(new FavoriteRepository.ItemRef("s", Key("fav")),
            new FavoriteRepository(_db).ListItems(new FavoriteRepository(_db).EnsureDefault()));

        // 报告口径：留了 2 份锚点（一份设置、一份收藏）
        Assert.Equal(2, result.AnchorsKept);
    }

    [Fact]
    public void Purge_KeepsFingerprintLedger_SoMetaFollowsToNewPath()
    {
        // 真文件：指纹算的是"大小 + 首尾 64KB"，内容一样就认得出是同一份
        var oldRel = "片/move.mp4";
        Directory.CreateDirectory(Path.Combine(_root, "片"));
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(Path.Combine(_root, "片", "move.mp4"), payload);

        var oldKey = MediaKey.ForSingle(oldRel);
        var oldEntry = new MediaEntry
        {
            SourceId = "s",
            Key = oldKey,
            RelPath = oldRel,
            RelDir = "片",
            Layout = SourceLayout.Single,
            TitleRaw = "move",
            SizeBytes = payload.Length,
        };
        new MediaRepository(_db).Upsert("s", [oldEntry]);
        FingerprintSync.Sync(_db, [oldEntry],
            new Dictionary<string, string> { [oldKey] = _root });
        new MetaRepository(_db).Apply("s", oldKey, new MetaPatch { Rotation = 270 });

        // 文件夹搬走 → 标缺失（这次一条都没扫到）→ 清理位置记录
        new MediaRepository(_db).MarkMissingExcept("s", []);
        new MissingPurge(_db).Run("s");
        Assert.Null(new MediaRepository(_db).Get("s", oldKey));

        // 换个目录搬回来：新 key（路径哈希变了），只可能靠指纹认回设置
        var newRel = "新/move.mp4";
        Directory.CreateDirectory(Path.Combine(_root, "新"));
        File.WriteAllBytes(Path.Combine(_root, "新", "move.mp4"), payload);
        var newKey = MediaKey.ForSingle(newRel);
        var newEntry = new MediaEntry
        {
            SourceId = "s",
            Key = newKey,
            RelPath = newRel,
            RelDir = "新",
            Layout = SourceLayout.Single,
            TitleRaw = "move",
            SizeBytes = payload.Length,
        };
        new MediaRepository(_db).Upsert("s", [newEntry]);
        FingerprintSync.Sync(_db, [newEntry],
            new Dictionary<string, string> { [newKey] = _root });

        // 位置记录删了，路标还在 → 设置跟到新路径
        Assert.Equal(270, new MetaRepository(_db).Get("s", newKey)!.Rotation);
    }

    [Fact]
    public void Purge_IsNoOpWhenNothingIsMissing()
    {
        var result = new MissingPurge(_db).Run("s");
        Assert.Equal(0, result.Deleted);
        Assert.Equal(3, _db.CountAvailable("s"));
    }

    public void Dispose()
    {
        try
        {
            _db.Dispose();
            Directory.Delete(_metaDir, recursive: true);
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响断言。
        }
    }
}
