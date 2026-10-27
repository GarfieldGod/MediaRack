using MediaRack.Library;

namespace MediaRack.Server.Tests;

/// <summary>
/// 导出产物的预算淘汰。这部分不碰 HTTP，直接测服务本身更清楚。
/// </summary>
public sealed class ExportServiceTests : IDisposable
{
    private readonly string _metaRoot = Path.Combine(
        Path.GetTempPath(), "mrack-export-" + Guid.NewGuid().ToString("N")[..10]);

    public ExportServiceTests() => Directory.CreateDirectory(_metaRoot);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_metaRoot)) Directory.Delete(_metaRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉不影响断言结果。
        }
    }

    private string WriteExport(string sourceId, string key, int size, DateTime lastWriteUtc)
    {
        var path = ExportService.PathFor(_metaRoot, sourceId, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void PathFor_KeepsSourceSegmentFilesystemSafe()
    {
        var path = ExportService.PathFor(_metaRoot, "我的 源/../x", "1001_c2002");

        Assert.DoesNotContain("..", path);
        Assert.EndsWith("1001_c2002.mp4", path);
        // 路径仍然落在 export 根之下——源标识来自外部输入，不能被它跳出目录。
        Assert.StartsWith(ExportService.ExportRoot(_metaRoot), path);
    }

    [Fact]
    public void Evict_DoesNothingWhenUnderBudget()
    {
        WriteExport("s", "k1", 100, DateTime.UtcNow);
        WriteExport("s", "k2", 100, DateTime.UtcNow);

        var service = new ExportService(_metaRoot, budgetBytes: 1024);

        Assert.Equal(0, service.Evict());
        Assert.NotNull(service.TryGetReady("s", "k1"));
        Assert.NotNull(service.TryGetReady("s", "k2"));
    }

    [Fact]
    public void Evict_RemovesOldestFirstUntilUnderBudget()
    {
        var now = DateTime.UtcNow;

        // 三个 100 字节，mtime 依次变新。
        WriteExport("s", "oldest", 100, now.AddMinutes(-30));
        WriteExport("s", "middle", 100, now.AddMinutes(-20));
        WriteExport("s", "newest", 100, now.AddMinutes(-10));

        var service = new ExportService(_metaRoot, budgetBytes: 250);

        Assert.Equal(1, service.Evict());
        Assert.Null(service.TryGetReady("s", "oldest"));
        Assert.NotNull(service.TryGetReady("s", "middle"));
        Assert.NotNull(service.TryGetReady("s", "newest"));
    }

    [Fact]
    public void Touch_MakesAnEntryTheMostRecentlyUsed()
    {
        var now = DateTime.UtcNow;

        WriteExport("s", "a", 100, now.AddMinutes(-30));
        WriteExport("s", "b", 100, now.AddMinutes(-20));

        var service = new ExportService(_metaRoot, budgetBytes: 150);

        // 刚播过 a，它就变成"最近使用"，被淘汰的应该是 b。
        var pathA = service.TryGetReady("s", "a")!;
        service.Touch(pathA);

        Assert.Equal(1, service.Evict());
        Assert.NotNull(service.TryGetReady("s", "a"));
        Assert.Null(service.TryGetReady("s", "b"));
    }

    [Fact]
    public void Evict_IgnoresTemporaryFiles()
    {
        // 正在被 ffmpeg 写的 .tmp 不能当成垃圾删掉。
        var temp = ExportService.PathFor(_metaRoot, "s", "building") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllBytes(temp, new byte[10_000]);

        var service = new ExportService(_metaRoot, budgetBytes: 1);

        Assert.Equal(0, service.Evict());
        Assert.True(File.Exists(temp));
    }
}
