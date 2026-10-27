using MediaRack.Audit;

namespace MediaRack.Tests;

/// <summary>
/// 快照比对器本身的测试——也就是"证明审计真的会报警"。
/// <para>
/// 一个永远返回"通过"的检查等于没有检查。所以这里逐个制造它应该抓住的改动：
/// 改一个字节、加文件、删文件、加目录。
/// </para>
/// </summary>
public sealed class SourceSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-snap", Guid.NewGuid().ToString("N")[..8]);

    public SourceSnapshotTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "world");
    }

    private string File1 => Path.Combine(_root, "a.txt");

    [Fact]
    public void IdenticalTree_DiffIsEmpty()
    {
        var diff = SnapshotDiff.Compare(
            SourceSnapshot.Take(_root), SourceSnapshot.Take(_root));

        Assert.True(diff.IsEmpty);
        Assert.Equal(0, diff.TotalChanges);
    }

    [Fact]
    public void AddedFile_IsDetected()
    {
        var before = SourceSnapshot.Take(_root);
        File.WriteAllText(Path.Combine(_root, "new.txt"), "x");
        var after = SourceSnapshot.Take(_root);

        var diff = SnapshotDiff.Compare(before, after);

        Assert.False(diff.IsEmpty);
        Assert.Single(diff.Added);
        Assert.Equal("new.txt", diff.Added[0].RelPath);
    }

    [Fact]
    public void RemovedFile_IsDetected()
    {
        var before = SourceSnapshot.Take(_root);
        File.Delete(Path.Combine(_root, "sub", "b.txt"));
        var after = SourceSnapshot.Take(_root);

        var diff = SnapshotDiff.Compare(before, after);

        Assert.Single(diff.Removed);
        Assert.Equal("sub/b.txt", diff.Removed[0].RelPath);
    }

    [Fact]
    public void AddedDirectory_IsDetected()
    {
        var before = SourceSnapshot.Take(_root);
        Directory.CreateDirectory(Path.Combine(_root, "newdir"));
        var after = SourceSnapshot.Take(_root);

        var diff = SnapshotDiff.Compare(before, after);

        Assert.False(diff.IsEmpty);
        Assert.Equal(["newdir"], diff.AddedDirectories);
    }

    [Fact]
    public void SameSizeByteFlip_IsDetectedByMtime()
    {
        var before = SourceSnapshot.Take(_root);
        FlipOneByte(File1);                       // 长度不变
        File.SetLastWriteTimeUtc(File1, DateTime.UtcNow.AddMinutes(5));   // 显式改时间，避免同 tick 抖动

        var diff = SnapshotDiff.Compare(before, SourceSnapshot.Take(_root));

        Assert.Single(diff.Modified);
        Assert.Equal("a.txt", diff.Modified[0].Before.RelPath);
    }

    [Fact]
    public void SameSizeByteFlip_WithRestoredMtime_OnlyMd5CatchesIt()
    {
        var beforeMeta = SourceSnapshot.Take(_root);
        var beforeHash = SourceSnapshot.Take(_root, FingerprintMode.Md5);

        var mtime = File.GetLastWriteTimeUtc(File1);
        FlipOneByte(File1);
        File.SetLastWriteTimeUtc(File1, mtime);    // 蓄意还原时间戳

        // 元数据指纹看不出来——这是它有意的取舍（快，但挡不住这招）
        Assert.True(SnapshotDiff.Compare(beforeMeta, SourceSnapshot.Take(_root)).IsEmpty);

        // md5 指纹抓住
        var diff = SnapshotDiff.Compare(beforeHash, SourceSnapshot.Take(_root, FingerprintMode.Md5));
        Assert.Single(diff.Modified);
        Assert.NotEqual(diff.Modified[0].Before.Hash, diff.Modified[0].After.Hash);
    }

    [Fact]
    public void RelativePaths_UseForwardSlash()
    {
        var snapshot = SourceSnapshot.Take(_root);

        Assert.Contains(snapshot.Files, f => f.RelPath == "sub/b.txt");
        Assert.DoesNotContain(snapshot.Files, f => f.RelPath.Contains('\\'));
    }

    [Fact]
    public void TotalBytes_IsSumOfFiles()
    {
        var snapshot = SourceSnapshot.Take(_root);

        Assert.Equal(10, snapshot.TotalBytes);   // "hello" + "world"
    }

    private static void FlipOneByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
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
