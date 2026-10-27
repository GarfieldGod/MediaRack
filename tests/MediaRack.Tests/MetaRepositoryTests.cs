using MediaRack.Models;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Tests;

/// <summary>
/// 虚拟层：来源可信，磁盘不动。这一组测试全部围绕三件事——
/// <b>权限闸门</b>、<b>rev 单调</b>、<b>覆盖与回退</b>。
/// </summary>
public sealed class MetaRepositoryTests : IDisposable
{
    private const string Key = "s_0123456789abcdef";

    private readonly string _metaDir = Path.Combine(
        Path.GetTempPath(), "mrack-meta", Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDatabase _db;
    private readonly MetaRepository _meta;
    private readonly SourceRepository _sources;

    public MetaRepositoryTests()
    {
        Directory.CreateDirectory(_metaDir);
        _db = LibraryDatabase.Open(_metaDir);
        _db.Migrate();
        _meta = new MetaRepository(_db);
        _sources = new SourceRepository(_db);

        _sources.Upsert(Source(VirtualWrite.Full));
        new MediaRepository(_db).Upsert("s",
        [
            new MediaEntry
            {
                SourceId = "s",
                Key = Key,
                RelPath = "a.mp4",
                Layout = SourceLayout.Single,
                TitleRaw = "原始标题",
            },
        ]);
    }

    private static SourceConfig Source(VirtualWrite level) => new()
    {
        Id = "s",
        Name = "库",
        Layout = SourceLayout.Single,
        Root = Path.Combine(Path.GetTempPath(), "mrack-meta-root"),
        VirtualWrite = level,
    };

    [Fact]
    public void Get_WithoutRow_ReturnsNull()
    {
        Assert.Null(_meta.Get("s", Key));
    }

    [Fact]
    public void Resolve_WithoutOverride_FallsBackToSourceTitle()
    {
        var resolved = _meta.Resolve("s", Key)!;

        Assert.Equal("原始标题", resolved.Title);
        Assert.False(resolved.TitleOverridden);
    }

    [Fact]
    public void Title_OverridesSourceTitle()
    {
        var result = _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "  新标题  " });

        Assert.True(result.Ok);
        Assert.Equal(1, result.Value!.Rev);
        Assert.Equal("新标题", result.Value.TitleDisplay);   // 首尾空白被裁掉
        Assert.Equal("新标题", _meta.Resolve("s", Key)!.Title);
    }

    [Fact]
    public void Title_EmptyString_ClearsOverride()
    {
        _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "改过" });
        var cleared = _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "   " });

        Assert.Null(cleared.Value!.TitleDisplay);
        Assert.Equal("原始标题", _meta.Resolve("s", Key)!.Title);
        Assert.False(_meta.Resolve("s", Key)!.TitleOverridden);
    }

    [Fact]
    public void Rev_IsMonotonic_AcrossWrites()
    {
        var revs = new List<long>();
        revs.Add(_meta.Apply("s", Key, new MetaPatch { TitleDisplay = "a" }).Value!.Rev);
        revs.Add(_meta.Apply("s", Key, new MetaPatch { TagsAdd = ["t"] }).Value!.Rev);
        revs.Add(_meta.Apply("s", Key, new MetaPatch { Hidden = true }).Value!.Rev);
        revs.Add(_meta.Apply("s", Key, new MetaPatch { Rotation = 90 }).Value!.Rev);
        revs.Add(_meta.Apply("s", Key, new MetaPatch { PlayPosMs = 1000 }).Value!.Rev);

        Assert.Equal([1L, 2, 3, 4, 5], revs);
    }

    [Fact]
    public void LastWriteWins_SameField()
    {
        _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "A 改的" });
        _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "B 改的" });

        // 在线写入经服务端串行，后到覆盖先到，不需要额外合并逻辑。
        Assert.Equal("B 改的", _meta.Resolve("s", Key)!.Title);
    }

    [Fact]
    public void ExpectedRev_Mismatch_ReturnsConflict_AndValueUnchanged()
    {
        _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "第一次" });   // rev 1
        _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "第二次" });   // rev 2

        // 拿着 rev 1 来写，服务端已是 rev 2
        var result = _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "过期写入" }, expectedRev: 1);

        Assert.Equal(WriteStatus.Conflict, result.Status);
        Assert.Equal(2, result.Value!.Rev);            // 返回的是服务端现值，便于客户端决定
        Assert.Equal("第二次", _meta.Resolve("s", Key)!.Title);
    }

    [Fact]
    public void ExpectedRev_Match_Applies()
    {
        var first = _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "x" });
        var second = _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "y" }, expectedRev: first.Value!.Rev);

        Assert.Equal(WriteStatus.Applied, second.Status);
        Assert.Equal(2, second.Value!.Rev);
    }

    [Theory]
    [InlineData(VirtualWrite.None, false)]
    [InlineData(VirtualWrite.Whitelist, false)]
    [InlineData(VirtualWrite.Full, true)]
    public void Title_RespectsVirtualWriteLevel(VirtualWrite level, bool allowed)
    {
        _sources.SetVirtualWrite("s", level);

        var result = _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "x" });

        Assert.Equal(allowed ? WriteStatus.Applied : WriteStatus.Forbidden, result.Status);
    }

    [Theory]
    [InlineData(VirtualWrite.None, false)]
    [InlineData(VirtualWrite.Whitelist, true)]
    [InlineData(VirtualWrite.Full, true)]
    public void Tags_AllowedFromWhitelistUp(VirtualWrite level, bool allowed)
    {
        _sources.SetVirtualWrite("s", level);

        var result = _meta.Apply("s", Key, new MetaPatch { TagsAdd = ["风景"] });

        Assert.Equal(allowed ? WriteStatus.Applied : WriteStatus.Forbidden, result.Status);
    }

    [Fact]
    public void Forbidden_LeavesNoRow()
    {
        _sources.SetVirtualWrite("s", VirtualWrite.None);

        _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "x" });

        // 闸门在写入之前，所以库里连一条空记录都不该有
        Assert.Null(_meta.Get("s", Key));
    }

    [Fact]
    public void Patch_MixingAllowedAndForbiddenFields_IsRejectedWhole()
    {
        _sources.SetVirtualWrite("s", VirtualWrite.Whitelist);

        // whitelist 允许标签、不允许标题：整个补丁必须一起拒绝，不能只写一半
        var result = _meta.Apply("s", Key, new MetaPatch
        {
            TitleDisplay = "标题",
            TagsAdd = ["标签"],
        });

        Assert.Equal(WriteStatus.Forbidden, result.Status);
        Assert.Null(_meta.Get("s", Key));
    }

    [Fact]
    public void Tags_AddRemoveDedupe_IgnoreCase()
    {
        _meta.Apply("s", Key, new MetaPatch { TagsAdd = ["风景", "4K"] });
        _meta.Apply("s", Key, new MetaPatch { TagsAdd = ["风景", "风景 "] });    // 重复 + 尾空格
        var tags = _meta.Apply("s", Key, new MetaPatch { TagsAdd = ["竖屏"] }).Value!.Tags;

        Assert.Equal(["风景", "4K", "竖屏"], tags);

        var afterRemove = _meta.Apply("s", Key, new MetaPatch { TagsRemove = ["4k"] }).Value!.Tags;
        Assert.Equal(["风景", "竖屏"], afterRemove);   // 忽略大小写命中
    }

    [Fact]
    public void Tags_ClearAndSet()
    {
        _meta.Apply("s", Key, new MetaPatch { TagsAdd = ["a", "b"] });

        var set = _meta.Apply("s", Key, new MetaPatch { Tags = ["c"] }).Value!.Tags;
        Assert.Equal(["c"], set);

        var cleared = _meta.Apply("s", Key, new MetaPatch { ClearTags = true }).Value!.Tags;
        Assert.Empty(cleared);
    }

    [Fact]
    public void Played_IncrementsCountAndSetsTimestamp()
    {
        _meta.Apply("s", Key, new MetaPatch { PlayPosMs = 5000, MarkedPlayed = true });
        var meta = _meta.Apply("s", Key, new MetaPatch { PlayPosMs = 9000, MarkedPlayed = true }).Value!;

        Assert.Equal(9000, meta.PlayPosMs);
        Assert.Equal(2, meta.PlayCount);
        Assert.NotNull(meta.LastPlayAt);
    }

    [Fact]
    public void EmptyPatch_DoesNotBumpRev()
    {
        var result = _meta.Apply("s", Key, new MetaPatch());

        Assert.True(result.Ok);
        Assert.Equal(0, result.Value!.Rev);
        Assert.Null(_meta.Get("s", Key));
    }

    [Fact]
    public void UnknownItem_NotFound()
    {
        Assert.Equal(WriteStatus.NotFound, _meta.Apply("s", "nope", new MetaPatch { Hidden = true }).Status);
    }

    [Fact]
    public void UnknownSource_NotFound()
    {
        Assert.Equal(WriteStatus.NotFound, _meta.Apply("nope", Key, new MetaPatch { Hidden = true }).Status);
    }

    [Fact]
    public void Rotation_MustBeMultipleOf90()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _meta.Apply("s", Key, new MetaPatch { Rotation = 45 }));
    }

    [Fact]
    public void Rotation_NormalizesNegativeAndOver360()
    {
        Assert.Equal(270, _meta.Apply("s", Key, new MetaPatch { Rotation = -90 }).Value!.Rotation);
        Assert.Equal(0, _meta.Apply("s", Key, new MetaPatch { Rotation = 360 }).Value!.Rotation);
    }

    [Fact]
    public void MetaSurvivesRescan()
    {
        _meta.Apply("s", Key, new MetaPatch { TitleDisplay = "改名了" });

        // 重扫：同样的条目再写一遍
        new MediaRepository(_db).Upsert("s",
        [
            new MediaEntry
            {
                SourceId = "s",
                Key = Key,
                RelPath = "a.mp4",
                Layout = SourceLayout.Single,
                TitleRaw = "原始标题",
            },
        ]);

        var resolved = _meta.Resolve("s", Key)!;
        Assert.Equal("改名了", resolved.Title);
        Assert.Equal("原始标题", resolved.TitleRaw);   // 源端值仍完整保留
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
