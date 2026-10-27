using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Tests;

public sealed class LibraryDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-tests", Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void Migrate_CreatesSchemaAtCurrentVersion()
    {
        using var db = LibraryDatabase.Open(_root);

        var result = db.Migrate();

        Assert.True(result.Changed);
        Assert.Equal(0, result.From);
        Assert.Equal(LibraryDatabase.SupportedVersion, result.To);
        Assert.Equal(LibraryDatabase.SupportedVersion, db.Version);
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        using var db = LibraryDatabase.Open(_root);
        db.Migrate();

        var second = db.Migrate();

        Assert.False(second.Changed);
        Assert.Empty(second.Applied);
    }

    [Fact]
    public void Migrate_CreatesAllExpectedTables()
    {
        using var db = LibraryDatabase.Open(_root);
        db.Migrate();

        var tables = db.TableNames();

        Assert.Equal(
            [
                "collection", "content_fp", "favorite", "favorite_collection", "favorite_item",
                "last_session", "media", "media_meta", "media_track",
                "schema_version", "source", "source_folder",
            ],
            tables);
    }

    [Fact]
    public void Open_WithoutCreate_ThrowsWhenMissing()
    {
        Assert.Throws<FileNotFoundException>(() => LibraryDatabase.Open(_root, create: false));
    }

    [Fact]
    public void Open_WithoutCreate_IsStillWritable()
    {
        // create:false 只表示"不许顺手建库"，不是只读。
        // 这条曾经踩过：mrack posters 用它打开库，抽帧成功却写不回 poster_rel。
        using (var db = LibraryDatabase.Open(_root)) db.Migrate();
        using var rw = LibraryDatabase.Open(_root, create: false);

        var repo = new MediaRepository(rw);
        var written = repo.Upsert("s",
        [
            new MediaEntry
            {
                SourceId = "s",
                Key = "s_abc",
                RelPath = "a.mp4",
                Layout = SourceLayout.Single,
            },
        ]);

        Assert.Equal(1, written);
        Assert.Equal(1, rw.CountMedia());
    }

    [Fact]
    public void Open_ReadOnly_RejectsWrites()
    {
        using (var db = LibraryDatabase.Open(_root)) db.Migrate();
        using var ro = LibraryDatabase.Open(_root, create: false, readOnly: true);

        var repo = new MediaRepository(ro);

        Assert.ThrowsAny<Exception>(() => repo.Upsert("s",
        [
            new MediaEntry
            {
                SourceId = "s",
                Key = "s_abc",
                RelPath = "a.mp4",
                Layout = SourceLayout.Single,
            },
        ]));
    }

    [Fact]
    public void EmptyLibrary_HasNoMedia()
    {
        using var db = LibraryDatabase.Open(_root);
        db.Migrate();

        Assert.Equal(0, db.CountMedia());
        Assert.Equal(0, db.CountRows("source"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // 测试清理失败不应让测试变红。
            }
        }
    }
}

public sealed class MediaKeyTests
{
    [Fact]
    public void ForDual_MatchesSampleFromDisk()
    {
        // 真实样本：test/114053930288640/c_28543681073/120/
        Assert.Equal("114053930288640_c28543681073", MediaKey.ForDual(114053930288640, 28543681073));
    }

    [Fact]
    public void TryParseDual_RoundTrips()
    {
        var key = MediaKey.ForDual(114053930288640, 28543681073);

        Assert.True(MediaKey.TryParseDual(key, out var avid, out var cid));
        Assert.Equal("114053930288640", avid);
        Assert.Equal("28543681073", cid);
    }

    [Fact]
    public void TryParseDual_RejectsSingleKey()
    {
        Assert.False(MediaKey.TryParseDual(MediaKey.ForSingle(@"A\b.mp4"), out _, out _));
    }

    [Fact]
    public void ForSingle_IsStableAcrossSeparatorsAndCase()
    {
        var a = MediaKey.ForSingle(@"Season 1\E01.mp4");
        var b = MediaKey.ForSingle("season 1/e01.mp4");

        Assert.Equal(a, b);
        Assert.StartsWith(MediaKey.SinglePrefix, a);
        Assert.Equal(18, a.Length); // s_ + 16 hex
    }

    [Fact]
    public void ForSingle_DiffersByPath()
    {
        Assert.NotEqual(MediaKey.ForSingle("a/x.mp4"), MediaKey.ForSingle("b/x.mp4"));
    }
}
