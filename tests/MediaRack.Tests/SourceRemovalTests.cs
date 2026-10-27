using MediaRack.Library;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Tests;

/// <summary>
/// 删除源的两层语义：
/// <list type="bullet">
/// <item>位置层（源登记 / 文件夹 / 条目 / 合集投影）总是删；</item>
/// <item>档案层（meta 与收藏）默认保留——它是"文件回来还能认回设置"的唯一凭据，
/// 勾了"连档案一起删"才一起抹掉。</item>
/// </list>
/// 邻源必须一个字节都不受影响，所以每个用例都放了第二个源做对照。
/// </summary>
public sealed class SourceRemovalTests : IDisposable
{
    private readonly string _metaDir = Path.Combine(
        Path.GetTempPath(), "mrack-del", Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-del-root", Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDatabase _db;
    private readonly MediaEntry _victim;
    private readonly MediaEntry _neighbour;

    public SourceRemovalTests()
    {
        Directory.CreateDirectory(_metaDir);
        Directory.CreateDirectory(_root);
        _db = LibraryDatabase.Open(_metaDir);
        _db.Migrate();

        var sources = new SourceRepository(_db);
        foreach (var id in new[] { "victim", "neighbour" })
        {
            sources.Upsert(new SourceConfig
            {
                Id = id,
                Name = id,
                Layout = SourceLayout.Single,
                Root = _root,
                VirtualWrite = VirtualWrite.Full,
            });
            sources.AddFolder(id, Path.Combine(_root, id), SourceLayout.Single);
        }

        _victim = Entry("victim", "片/被删.mp4", 100);
        _neighbour = Entry("neighbour", "片/邻居.mp4", 200);

        new MediaRepository(_db).Upsert("victim", [_victim]);
        new MediaRepository(_db).Upsert("neighbour", [_neighbour]);
        new CollectionRebuilder(_db).Rebuild();

        new MetaRepository(_db).Apply("victim", _victim.Key, new MetaPatch { Rotation = 90 });
        new MetaRepository(_db).Apply("neighbour", _neighbour.Key, new MetaPatch { Rotation = 180 });

        var fav = new FavoriteRepository(_db);
        var favId = fav.EnsureDefault();
        fav.AddItems(favId, [new FavoriteRepository.ItemRef("victim", _victim.Key)]);
    }

    private static MediaEntry Entry(string sourceId, string relPath, long size) => new()
    {
        SourceId = sourceId,
        Key = MediaKey.ForSingle(relPath),
        RelPath = relPath,
        RelDir = "片",
        Layout = SourceLayout.Single,
        TitleRaw = Path.GetFileNameWithoutExtension(relPath),
        SizeBytes = size,
    };

    [Fact]
    public void Delete_RemovesPositions_ButKeepsArchiveAndNeighbour()
    {
        var result = new SourceRemoval(_db).Run("victim");

        Assert.NotNull(result);
        Assert.Equal(1, result!.MediaRemoved);
        Assert.Equal(1, result.FoldersRemoved);
        Assert.Equal(0, result.MetaRemoved);      // 没勾"连档案一起删"
        Assert.Equal(1, result.MetaKept);         // 档案留着
        Assert.Equal(1, result.FavoriteItemsKept);

        // 源与位置记录都没了
        Assert.Null(new SourceRepository(_db).Get("victim"));
        Assert.Equal(0, _db.CountMedia("victim"));
        Assert.Empty(new CollectionRepository(_db).ListBySource("victim"));

        // 档案与收藏原地不动
        Assert.Equal(90, new MetaRepository(_db).Get("victim", _victim.Key)!.Rotation);
        var fav = new FavoriteRepository(_db);
        Assert.Contains(new FavoriteRepository.ItemRef("victim", _victim.Key), fav.ListItems(fav.EnsureDefault()));

        // 邻源一行没动
        Assert.NotNull(new SourceRepository(_db).Get("neighbour"));
        Assert.Equal(1, _db.CountAvailable("neighbour"));
        Assert.Equal(180, new MetaRepository(_db).Get("neighbour", _neighbour.Key)!.Rotation);
    }

    [Fact]
    public void Delete_KeepsFingerprintLedger_SoArchiveStillFindsTheFile()
    {
        // 有指纹账才有"换路径也认回设置"：删源不能把这条账清掉。
        var fp = WriteVictimFile();
        FingerprintSync.Sync(_db, [_victim], new Dictionary<string, string> { [_victim.Key] = _root });

        new SourceRemoval(_db).Run("victim");

        Assert.Equal(("victim", _victim.Key), FingerprintSync.Locate(_db, fp));
    }

    [Fact]
    public void Delete_WithArchive_RemovesSettingsAndLedger()
    {
        var fp = WriteVictimFile();
        FingerprintSync.Sync(_db, [_victim], new Dictionary<string, string> { [_victim.Key] = _root });

        var result = new SourceRemoval(_db).Run("victim", withArchive: true);

        Assert.NotNull(result);
        Assert.Equal(1, result!.MetaRemoved);
        Assert.Equal(1, result.FavoriteItemsRemoved);
        Assert.Equal(0, result.MetaKept);
        Assert.Equal(1, result.FingerprintsRemoved);   // 无处可指的账一起收掉

        Assert.Null(FingerprintSync.Locate(_db, fp));
        Assert.Null(new MetaRepository(_db).Get("victim", _victim.Key));
        var fav = new FavoriteRepository(_db);
        Assert.Empty(fav.ListItems(fav.EnsureDefault()));

        // 邻源的设置不受影响
        Assert.Equal(180, new MetaRepository(_db).Get("neighbour", _neighbour.Key)!.Rotation);
    }

    /// <summary>落一个真文件（指纹算的是大小 + 首尾 64KB，内容得是真的），返回它的指纹。</summary>
    private string WriteVictimFile()
    {
        var path = Path.Combine(_root, _victim.RelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(path, payload);
        return ContentFingerprint.Compute(path);
    }

    [Fact]
    public void Delete_UnknownSource_IsNull()
    {
        Assert.Null(new SourceRemoval(_db).Run("nope"));
        Assert.NotNull(new SourceRepository(_db).Get("victim"));
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
