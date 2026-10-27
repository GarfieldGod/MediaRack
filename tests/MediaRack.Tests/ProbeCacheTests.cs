using MediaRack.Library;
using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Store;

namespace MediaRack.Tests;

/// <summary>
/// mtime 缓存（文件没变跳过 ffprobe）的单元测试。
/// <para>
/// 不依赖 ffmpeg：探测数据用手造的 MediaEntry 直接落库（模拟上一轮扫描的产物），
/// 文件 mtime/size 与磁盘真实 stat 对齐，验证缓存命中/失效与回放。
/// </para>
/// </summary>
public sealed class ProbeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-probe-cache", Guid.NewGuid().ToString("N")[..8]);
    private readonly string _meta = Path.Combine(
        Path.GetTempPath(), "mrack-probe-cache", Guid.NewGuid().ToString("N")[..8]);
    private readonly LibraryDatabase _db;

    public ProbeCacheTests()
    {
        Directory.CreateDirectory(_root);
        _db = LibraryDatabase.Open(_meta);
        _db.Migrate();
    }

    private SourceConfig Source => new()
    {
        Id = "cachetest",
        Name = "cachetest",
        Layout = SourceLayout.Single,
        Root = _root,
    };

    /// <summary>写一个文件并返回与磁盘 stat 对齐的、带 probe 数据的条目。</summary>
    private MediaEntry SeedFile(string relPath, int size = 2048)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);

        var info = new FileInfo(full);
        return new MediaEntry
        {
            SourceId = "cachetest",
            Key = MediaKey.ForSingle(relPath),
            RelPath = relPath,
            Layout = SourceLayout.Single,
            TitleRaw = Path.GetFileNameWithoutExtension(relPath),
            SizeBytes = info.Length,
            FileMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            DurationMs = 6000,
            Width = 2160,
            Height = 3840,
            Codec = "hevc",
            Status = MediaStatus.Ok,
            ScannedAt = 1,
        };
    }

    /// <summary>计数装饰：统计真正落到底层探测的次数（缓存命中不计）。</summary>
    private sealed class CountingProbe(IMediaProbe inner) : IMediaProbe
    {
        public int Calls;

        public bool TryProbe(string path, out ProbeResult result, out string? error)
        {
            Calls++;
            return inner.TryProbe(path, out result, out error);
        }
    }

    private ScanAttempt RescanWith(ProbeCache cache, CountingProbe counting) =>
        ScanService.ScanOnly(Source, VerifyMode.None, decorateProbe: (_, _) =>
            new CachedProbe(counting, cache));

    [Fact]
    public void Rescan_UnchangedFile_SkipsProbe_AndReplaysCachedMetadata()
    {
        var a = SeedFile("a.mp4");
        var b = SeedFile("b.mp4");
        Assert.Equal(2, new MediaRepository(_db).Upsert("cachetest", [a, b]));

        var cache = ProbeCache.Load(_db, "cachetest", _root);
        Assert.Equal(2, cache.Count);

        // 第二次扫描：mtime+size 未变 → 缓存命中 → 底层探测零调用，元数据从缓存回放
        var counting = new CountingProbe(new FakeProbe());
        var scan2 = RescanWith(cache, counting);

        Assert.Equal(0, counting.Calls);
        Assert.All(scan2.Result.Entries, e =>
        {
            Assert.Equal(6000, e.DurationMs);
            Assert.Equal(2160, e.Width);
            Assert.Equal(3840, e.Height);
            Assert.Equal("hevc", e.Codec);
            Assert.NotNull(e.FileMtime);
        });
    }

    [Fact]
    public void Rescan_ModifiedFile_IsProbedAgain()
    {
        var seeded = SeedFile("a.mp4");
        new MediaRepository(_db).Upsert("cachetest", [seeded]);

        // 改写内容：mtime/size 变了 → 缓存失效 → 必须重新探测
        Thread.Sleep(20); // 保证 mtime 有可见差异
        File.WriteAllBytes(Path.Combine(_root, "a.mp4"), new byte[4096]);

        var counting = new CountingProbe(new FakeProbe());
        var scan2 = RescanWith(ProbeCache.Load(_db, "cachetest", _root), counting);

        Assert.Equal(1, counting.Calls);
        Assert.Equal(4096, scan2.Result.Entries.Single(e => e.RelPath == "a.mp4").SizeBytes);
    }

    [Fact]
    public void Run_PersistsFileMtime_EvenWhenProbeFails()
    {
        var full = Path.Combine(_root, "a.mp4");
        File.WriteAllBytes(full, new byte[2048]);

        // 真实 ScanService 全流程（本机 ffprobe 对假字节会探测失败）——
        // 即便 probe 失败，file_mtime 也必须落库（它来自 stat，与探测无关）。
        var outcome = new ScanService(_db, _meta).Run(Source, new ScanOptions(Posters: null));
        Assert.Equal(1, outcome.Written);

        var row = new MediaRepository(_db).Get("cachetest", MediaKey.ForSingle("a.mp4"));
        Assert.NotNull(row);
        Assert.NotNull(row!.FileMtime);
    }

    [Fact]
    public void Cache_IgnoresCorruptRows()
    {
        var full = Path.Combine(_root, "bad.mp4");
        File.WriteAllBytes(full, new byte[2048]);
        var info = new FileInfo(full);

        var corrupt = new MediaEntry
        {
            SourceId = "cachetest",
            Key = MediaKey.ForSingle("bad.mp4"),
            RelPath = "bad.mp4",
            Layout = SourceLayout.Single,
            TitleRaw = "bad",
            SizeBytes = info.Length,
            FileMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            DurationMs = 6000,
            Width = 100,
            Height = 200,
            Status = MediaStatus.Corrupt,
            ScannedAt = 1,
        };
        new MediaRepository(_db).Upsert("cachetest", [corrupt]);

        // 损坏行即便有完整 probe 值与 mtime 也不得进缓存（会伪命中）
        var cache = ProbeCache.Load(_db, "cachetest", _root);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(Path.GetFullPath(full), info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(), out _));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(Path.Combine(Path.GetTempPath(), "mrack-probe-cache")))
            {
                Directory.Delete(Path.Combine(Path.GetTempPath(), "mrack-probe-cache"), recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }
}
