using System.Text;
using MediaRack.Models;
using MediaRack.Scan;

namespace MediaRack.Tests;

/// <summary>
/// 扫描进度的语义测试。前端拿这些数画进度条，两处最容易错的地方必须有锁：
/// 「总数未知（total=0）不能算出 100%」和「最后一条必须报到 done == total」。
/// </summary>
public sealed class ScanProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-prog", Guid.NewGuid().ToString("N")[..8]);

    public ScanProgressTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private sealed class Collector : IProgress<ScanProgress>
    {
        private readonly List<ScanProgress> _all = [];
        public IReadOnlyList<ScanProgress> All => _all;
        public void Report(ScanProgress value) => _all.Add(value);
    }

    private SourceConfig DualSource => new()
    {
        Id = "test", Name = "test", Layout = SourceLayout.Dual, Root = _root,
    };

    private SourceConfig SingleSource => new()
    {
        Id = "test", Name = "test", Layout = SourceLayout.Single, Root = _root,
    };

    /// <summary>造一个最小的 dual 条目目录（entry.json + video.m4s），返回条目目录。</summary>
    private string BuildDualItem(long avid, long cid)
    {
        var itemDir = Path.Combine(_root, avid.ToString(), $"c_{cid}");
        var tagDir = Path.Combine(itemDir, "120");
        Directory.CreateDirectory(tagDir);

        File.WriteAllBytes(Path.Combine(tagDir, "video.m4s"), Encoding.UTF8.GetBytes("VIDEO"));
        File.WriteAllText(Path.Combine(itemDir, "entry.json"), $$"""
        { "title": "T{{cid}}", "avid": {{avid}}, "type_tag": "120",
          "has_dash_audio": false, "page_data": { "cid": {{cid}} } }
        """);
        return itemDir;
    }

    [Fact]
    public void DualScan_ReportsEveryItem_AndEndsAtTotal()
    {
        BuildDualItem(1001, 2001);
        BuildDualItem(1002, 2002);
        BuildDualItem(1003, 2003);

        var collector = new Collector();
        var result = new DualScanner().Scan(DualSource, VerifyMode.None, collector);

        Assert.Equal(3, result.Entries.Count);

        var ticks = collector.All;
        Assert.Equal(3, ticks.Count);
        Assert.All(ticks, t => Assert.Equal(ScanStage.Scan, t.Stage));
        Assert.All(ticks, t => Assert.Equal(3, t.Total));

        // 逐条递增，最后一条必须报满——进度条停在 2/3 会让人以为卡住了
        Assert.Equal([1, 2, 3], ticks.Select(t => t.Done));
        Assert.Equal(100, ticks[^1].Percent);
    }

    [Fact]
    public void SingleScan_ReportsEveryFile_AndEndsAtTotal()
    {
        Directory.CreateDirectory(Path.Combine(_root, "合集"));
        File.WriteAllBytes(Path.Combine(_root, "合集", "a.mp4"), new byte[64]);
        File.WriteAllBytes(Path.Combine(_root, "合集", "b.mp4"), new byte[64]);
        File.WriteAllBytes(Path.Combine(_root, "c.mp4"), new byte[64]);

        var collector = new Collector();
        var result = new SingleScanner().Scan(SingleSource, collector);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal([1, 2, 3], collector.All.Select(t => t.Done));
        Assert.All(collector.All, t => Assert.Equal(3, t.Total));
    }

    [Fact]
    public void UnknownTotal_IsNotOneHundredPercent()
    {
        // 写库 / 抽帧阶段只知道"做了多少"，不知道总量。
        // 0/0 若按 done>=total 判断就会显示满格——必须保持 0 并走不确定动画。
        var unknown = new ScanProgress(ScanStage.Posters, 12, 0);
        Assert.Equal(0, unknown.Percent);

        var known = new ScanProgress(ScanStage.Scan, 1, 3);
        Assert.Equal(33, known.Percent);

        // 上报偶尔会迟到一拍（done 略超 total），也不能越过 100
        Assert.Equal(100, new ScanProgress(ScanStage.Scan, 5, 4).Percent);
    }
}
