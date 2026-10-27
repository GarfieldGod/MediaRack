using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Scan;

namespace MediaRack.Tests;

/// <summary>
/// single 源扫描测试。全部使用临时目录 + 假探测，不依赖 ffmpeg，因此可在任何环境跑。
/// </summary>
public sealed class SingleScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-single", Guid.NewGuid().ToString("N")[..8]);

    public SingleScannerTests() => Directory.CreateDirectory(_root);

    private SourceConfig Source => new()
    {
        Id = "test",
        Name = "test",
        Layout = SourceLayout.Single,
        Root = _root,
    };

    private string Touch(string relativePath, int size = 1024)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
        return full;
    }

    [Fact]
    public void Scan_UsesFilenameAsTitle_AndDirectoryAsGroup()
    {
        Touch("合集 一/第一集.mp4");
        Touch("合集 一/第二集.mkv");
        Touch("根目录散落.mp4");

        var result = new SingleScanner(new FakeProbe()).Scan(Source);

        Assert.Equal(3, result.Entries.Count);

        var inCollection = result.Entries.Where(e => e.RelDir == "合集 一").ToList();
        Assert.Equal(2, inCollection.Count);
        Assert.Contains(inCollection, e => e.TitleRaw == "第一集");
        Assert.Contains(inCollection, e => e.TitleRaw == "第二集");

        var loose = result.Entries.Single(e => e.TitleRaw == "根目录散落");
        Assert.Null(loose.RelDir); // 根目录散落文件不属于任何子目录
        Assert.Equal("根目录散落.mp4", loose.RelPath);
    }

    [Fact]
    public void Scan_UsesNormalizedForwardSlashPaths_AndSingleKeyPrefix()
    {
        Touch("A/B/c.mp4");

        var entry = new SingleScanner(new FakeProbe()).Scan(Source).Entries.Single();

        Assert.Equal("A/B/c.mp4", entry.RelPath);
        Assert.Equal("A/B", entry.RelDir);
        Assert.StartsWith(MediaKey.SinglePrefix, entry.Key);
        Assert.Equal(SourceLayout.Single, entry.Layout);
    }

    [Fact]
    public void Scan_SameRelativePathDifferentRoot_YieldsSameKey()
    {
        Touch("x/y.mp4");
        var a = new SingleScanner(new FakeProbe()).Scan(Source).Entries.Single().Key;

        var otherRoot = Path.Combine(_root, "另一块盘");
        Directory.CreateDirectory(Path.Combine(otherRoot, "x"));
        File.WriteAllBytes(Path.Combine(otherRoot, "x", "y.mp4"), new byte[16]);

        var other = new SourceConfig
        {
            Id = "other",
            Name = "other",
            Layout = SourceLayout.Single,
            Root = otherRoot,
        };
        var b = new SingleScanner(new FakeProbe()).Scan(other).Entries.Single().Key;

        // 键来自相对路径而不是绝对路径：换盘符、换机器都不失效
        Assert.Equal(a, b);
    }

    [Fact]
    public void Scan_ChineseAndSpacePaths_AreIndexed()
    {
        Touch("中文 目录 名/第 一 集 视频.mp4");

        var entry = new SingleScanner(new FakeProbe()).Scan(Source).Entries.Single();

        Assert.Equal("第 一 集 视频", entry.TitleRaw);
        Assert.Equal("中文 目录 名", entry.RelDir);
    }

    [Fact]
    public void Scan_ProbeFailure_StillIndexesWithoutMetadata()
    {
        Touch("坏文件.mp4");
        Touch("好文件.mp4");

        var result = new SingleScanner(new FakeProbe(failOn: "坏文件")).Scan(Source);

        Assert.Equal(2, result.Entries.Count); // 读不出元数据也必须入库
        var bad = result.Entries.Single(e => e.TitleRaw == "坏文件");
        Assert.Null(bad.Width);
        Assert.Equal(MediaStatus.Ok, bad.Status);
        Assert.Contains(result.Report.Problems, p => p.RelPath == "坏文件.mp4");
    }

    [Fact]
    public void Scan_ZeroByteFile_IsCorrupt()
    {
        Touch("空文件.mp4", size: 0);

        var result = new SingleScanner(new FakeProbe()).Scan(Source);

        Assert.Equal(MediaStatus.Corrupt, result.Entries.Single().Status);
        Assert.Contains(result.Report.Problems, p => p.Message.Contains("0 字节"));
    }

    [Fact]
    public void Scan_SkipsHiddenAndJunkDirectories()
    {
        Touch("正片.mp4");
        Touch(".git/hidden.mp4");
        Touch(".videoview/meta.mp4");
        Touch("$RECYCLE.BIN/deleted.mp4");
        Touch("System Volume Information/sys.mp4");

        var result = new SingleScanner(new FakeProbe()).Scan(Source);

        Assert.Single(result.Entries);
        Assert.Equal("正片", result.Entries[0].TitleRaw);
    }

    [Fact]
    public void Scan_IgnoresNonVideoExtensions()
    {
        Touch("视频.mp4");
        Touch("说明.txt");
        Touch("封面.jpg");
        Touch("弹幕.xml");

        var result = new SingleScanner(new FakeProbe()).Scan(Source);

        Assert.Single(result.Entries);
    }

    [Fact]
    public void Scan_NoProbe_IndexesWithoutMetadata()
    {
        Touch("视频.mp4");

        var result = new SingleScanner().Scan(Source);

        var entry = result.Entries.Single();
        Assert.Equal(MediaStatus.Ok, entry.Status);
        Assert.Null(entry.Width);
        Assert.Single(entry.Tracks);
        Assert.Equal(TrackRole.Video, entry.Tracks[0].Role);
    }

    [Fact]
    public void Scan_RecordsFileSizeAsSingleTrack()
    {
        Touch("视频.mp4", size: 4096);

        var entry = new SingleScanner(new FakeProbe()).Scan(Source).Entries.Single();

        Assert.Equal(4096, entry.SizeBytes);
        Assert.Equal(4096, entry.Tracks.Single().SizeBytes);
        Assert.Null(entry.Tracks.Single().Md5); // 单流文件没有完整性契约
    }

    [Fact]
    public void Scan_NonSingleLayout_Throws()
    {
        var dual = new SourceConfig
        {
            Id = "d",
            Name = "d",
            Layout = SourceLayout.Dual,
            Root = _root,
        };

        Assert.Throws<ArgumentException>(() => new SingleScanner(new FakeProbe()).Scan(dual));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }
}

/// <summary>可控的假探测：不发外部进程，按文件名决定成败与返回值。</summary>
internal sealed class FakeProbe(string? failOn = null) : IMediaProbe
{
    public bool TryProbe(string path, out ProbeResult result, out string? error)
    {
        var name = Path.GetFileName(path);

        if (failOn is not null && name.Contains(failOn, StringComparison.Ordinal))
        {
            result = new ProbeResult(null, null, null, 0, null, false);
            error = "moov atom not found";
            return false;
        }

        result = new ProbeResult(
            DurationMs: 6000,
            Width: 2160,
            Height: 3840,
            Rotation: 0,
            Codec: "hevc",
            HasAudio: true);
        error = null;
        return true;
    }
}
