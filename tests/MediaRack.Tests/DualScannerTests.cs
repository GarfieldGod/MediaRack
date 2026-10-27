using System.Security.Cryptography;
using System.Text;
using MediaRack.Models;
using MediaRack.Scan;

namespace MediaRack.Tests;

/// <summary>
/// 扫描器测试。全部使用运行时生成的最小样本，不依赖磁盘上的真实缓存目录。
/// </summary>
public sealed class DualScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-scan", Guid.NewGuid().ToString("N")[..8]);

    private const long Avid = 114053930288640;
    private const long Cid = 28543681073;

    public DualScannerTests() => Directory.CreateDirectory(_root);

    private SourceConfig Source => new()
    {
        Id = "test",
        Name = "test",
        Layout = SourceLayout.Dual,
        Root = _root,
    };

    /// <summary>造一个最小但结构完整的条目目录，返回其路径。</summary>
    private string BuildItem(
        long avid = Avid, long cid = Cid,
        bool withAudio = true, bool withIndex = true,
        string videoContent = "VIDEO-DATA-0123456789")
    {
        var itemDir = Path.Combine(_root, avid.ToString(), $"c_{cid}");
        var tagDir = Path.Combine(itemDir, "120");
        Directory.CreateDirectory(tagDir);

        File.WriteAllBytes(Path.Combine(tagDir, "video.m4s"), Encoding.UTF8.GetBytes(videoContent));
        File.WriteAllText(Path.Combine(itemDir, "danmaku.xml"), "<i/>");

        var audioContent = "AUDIO-DATA";
        var total = Encoding.UTF8.GetByteCount(videoContent);

        if (withAudio)
        {
            File.WriteAllBytes(Path.Combine(tagDir, "audio.m4s"), Encoding.UTF8.GetBytes(audioContent));
            total += Encoding.UTF8.GetByteCount(audioContent);
        }

        var entry = $$"""
        {
          "title": "测试视频",
          "avid": {{avid}},
          "bvid": "BV1TEST",
          "type_tag": "120",
          "total_time_milli": 14442,
          "total_bytes": {{total}},
          "has_dash_audio": {{(withAudio ? "true" : "false")}},
          "is_completed": true,
          "owner_name": "测试UP",
          "owner_id": 1018949735,
          "page_data": { "cid": {{cid}}, "page": 1, "width": 2160, "height": 3840 }
        }
        """;
        File.WriteAllText(Path.Combine(itemDir, "entry.json"), entry);

        if (withIndex)
        {
            var index = $$"""
            {
              "video": [{ "size": {{Encoding.UTF8.GetByteCount(videoContent)}},
                          "md5": "{{Md5(Encoding.UTF8.GetBytes(videoContent))}}",
                          "bandwidth": 3815346, "codecid": 12, "width": 2160, "height": 3840 }],
              "audio": [{ "size": {{Encoding.UTF8.GetByteCount(audioContent)}},
                          "md5": "{{Md5(Encoding.UTF8.GetBytes(audioContent))}}",
                          "bandwidth": 79411, "codecid": 0 }]
            }
            """;
            File.WriteAllText(Path.Combine(tagDir, "index.json"), index);
        }

        return itemDir;
    }

    private static string Md5(byte[] data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    [Fact]
    public void Scan_HealthyItem_IsOk()
    {
        BuildItem();

        var result = new DualScanner().Scan(Source, VerifyMode.Full);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(MediaStatus.Ok, entry.Status);
        Assert.Equal($"{Avid}_c{Cid}", entry.Key);
        Assert.Equal($"{Avid}/c_{Cid}", entry.RelPath);
        Assert.Equal("测试视频", entry.TitleRaw);
        Assert.Equal("测试UP", entry.UpName);
        Assert.Equal("hevc", entry.Codec);
        Assert.Equal(120, entry.TagCode);
        Assert.Equal(2160, entry.Width);
        Assert.True(entry.HasDanmaku);
        Assert.Empty(result.Report.Problems);
    }

    [Fact]
    public void Scan_RecordsBothTracks()
    {
        BuildItem();

        var entry = new DualScanner().Scan(Source, VerifyMode.Full).Entries.Single();

        Assert.Equal(2, entry.Tracks.Count);
        Assert.Contains(entry.Tracks, t => t.Role == TrackRole.Video && t.CodecId == 12);
        Assert.Contains(entry.Tracks, t => t.Role == TrackRole.Audio);
        Assert.All(entry.Tracks, t => Assert.False(string.IsNullOrEmpty(t.Md5)));
    }

    [Fact]
    public void Scan_ByteFlipped_IsCorruptUnderFull()
    {
        var itemDir = BuildItem();
        var videoPath = Path.Combine(itemDir, "120", "video.m4s");

        // 翻一个字节但保持长度不变：size 校验会放过，只有 md5 能抓住
        var bytes = File.ReadAllBytes(videoPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(videoPath, bytes);

        var result = new DualScanner().Scan(Source, VerifyMode.Full);

        Assert.Equal(MediaStatus.Corrupt, result.Entries.Single().Status);
        Assert.Contains(result.Report.Problems, p => p.Message.Contains("md5 不符"));
    }

    [Fact]
    public void Scan_ByteFlipped_PassesSizeOnlyVerify()
    {
        var itemDir = BuildItem();
        var videoPath = Path.Combine(itemDir, "120", "video.m4s");
        var bytes = File.ReadAllBytes(videoPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(videoPath, bytes);

        var result = new DualScanner().Scan(Source, VerifyMode.Sample);

        // sample 只比字节数，改内容不改长度时按设计放过——这正是 full 存在的理由
        Assert.Equal(MediaStatus.Ok, result.Entries.Single().Status);
    }

    [Fact]
    public void Scan_SizeMismatch_IsCorrupt()
    {
        BuildItem();
        var videoPath = Path.Combine(_root, Avid.ToString(), $"c_{Cid}", "120", "video.m4s");
        File.AppendAllText(videoPath, "EXTRA");

        var result = new DualScanner().Scan(Source, VerifyMode.Sample);

        Assert.Equal(MediaStatus.Corrupt, result.Entries.Single().Status);
        Assert.Contains(result.Report.Problems, p => p.Message.Contains("大小不符"));
    }

    [Fact]
    public void Scan_MissingAudio_IsCorrupt()
    {
        var itemDir = BuildItem(withAudio: false);
        File.Delete(Path.Combine(itemDir, "120", "audio.m4s"));

        // entry 里 has_dash_audio 为 false，删掉 audio 不应算损坏
        var result = new DualScanner().Scan(Source, VerifyMode.Full);
        Assert.Equal(MediaStatus.Ok, result.Entries.Single().Status);
    }

    [Fact]
    public void Scan_MissingVideo_IsMissing()
    {
        var itemDir = BuildItem();
        File.Delete(Path.Combine(itemDir, "120", "video.m4s"));
        File.Delete(Path.Combine(itemDir, "120", "audio.m4s"));

        var result = new DualScanner().Scan(Source, VerifyMode.Full);

        Assert.Equal(MediaStatus.Missing, result.Entries.Single().Status);
    }

    [Fact]
    public void Scan_WithoutIndexJson_StillIndexesButWarns()
    {
        BuildItem(withIndex: false);

        var result = new DualScanner().Scan(Source, VerifyMode.Full);

        Assert.Equal(MediaStatus.Ok, result.Entries.Single().Status);
        Assert.Contains(result.Report.Problems, p => p.Message.Contains("index.json"));
    }

    [Fact]
    public void Scan_BrokenEntryJson_SkipsOnlyThatItem()
    {
        var bad = Path.Combine(_root, "1", "c_1");
        Directory.CreateDirectory(Path.Combine(bad, "120"));
        File.WriteAllText(Path.Combine(bad, "entry.json"), "{ this is not json");
        BuildItem();

        var result = new DualScanner().Scan(Source, VerifyMode.Full);

        Assert.Single(result.Entries); // 好条目仍然入库
        Assert.Contains(result.Report.Problems, p => p.Fatal);
    }

    [Fact]
    public void Scan_EmptyRoot_ReturnsEmptyReport()
    {
        var result = new DualScanner().Scan(Source, VerifyMode.Full);

        Assert.Empty(result.Entries);
        Assert.Equal(0, result.Report.TotalScanned);
    }

    [Fact]
    public void Scan_SingleItemRoot_IsSupported()
    {
        var itemDir = BuildItem();
        var single = new SourceConfig
        {
            Id = "test",
            Name = "test",
            Layout = SourceLayout.Dual,
            Root = itemDir,
        };

        var result = new DualScanner().Scan(single, VerifyMode.Full);

        Assert.Single(result.Entries);
        Assert.Equal(".", result.Entries[0].RelPath.Replace('\\', '/'));
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
                // 清理失败不影响结论
            }
        }
    }
}
