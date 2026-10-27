using MediaRack.Models;
using MediaRack.Scan;

namespace MediaRack.Tests;

/// <summary>布局自动识别：让 <c>mrack scan</c> 不必手写 --layout。</summary>
public sealed class LayoutDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-layout", Guid.NewGuid().ToString("N")[..8]);

    public LayoutDetectorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void EmptyDirectory_IsSingle()
    {
        Assert.Equal(SourceLayout.Single, LayoutDetector.Detect(_root));
    }

    [Fact]
    public void BilibiliTree_IsDual()
    {
        var item = Path.Combine(_root, "114053930288640", "c_28543681073");
        Directory.CreateDirectory(Path.Combine(item, "120"));
        File.WriteAllText(Path.Combine(item, "entry.json"), "{}");

        Assert.Equal(SourceLayout.Dual, LayoutDetector.Detect(_root));
    }

    [Fact]
    public void RootIsSingleCacheItem_IsDual()
    {
        Directory.CreateDirectory(Path.Combine(_root, "120"));
        File.WriteAllText(Path.Combine(_root, "entry.json"), "{}");

        Assert.Equal(SourceLayout.Dual, LayoutDetector.Detect(_root));
    }

    [Fact]
    public void PlainVideoTree_IsSingle()
    {
        Directory.CreateDirectory(Path.Combine(_root, "合集"));
        File.WriteAllBytes(Path.Combine(_root, "合集", "a.mp4"), [0, 0, 0]);
        File.WriteAllBytes(Path.Combine(_root, "b.mp4"), [0, 0, 0]);

        Assert.Equal(SourceLayout.Single, LayoutDetector.Detect(_root));
    }

    [Fact]
    public void MissingDirectory_FallsBackToSingle()
    {
        Assert.Equal(SourceLayout.Single, LayoutDetector.Detect(Path.Combine(_root, "nope")));
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
