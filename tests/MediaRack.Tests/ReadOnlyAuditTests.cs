using MediaRack.Audit;
using MediaRack.Models;

namespace MediaRack.Tests;

/// <summary>
/// <c>verify-readonly</c> 的端到端测试：真的跑一整套操作，然后要求源目录一模一样。
/// <para>
/// 这一步是项目的"地基验收"——它一旦变红，说明某次改动引入了对源目录的写操作，
/// 无论那条路径看起来多无害。
/// </para>
/// </summary>
public sealed class ReadOnlyAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mrack-audit-test", Guid.NewGuid().ToString("N")[..8]);

    public ReadOnlyAuditTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "合集 A"));
        // 内容是假的也没关系：审计证明的是"没被写过"，不是"能不能解码"。
        File.WriteAllText(Path.Combine(_root, "one.mp4"), "not really a video");
        File.WriteAllText(Path.Combine(_root, "合集 A", "two.mp4"), "also not a video");
        File.WriteAllText(Path.Combine(_root, "笔记.txt"), "顺手放一个非视频文件");
    }

    private SourceConfig Source => new()
    {
        Id = "audit",
        Name = "审计源",
        Layout = SourceLayout.Single,
        Root = _root,
    };

    [Fact]
    public void Audit_SourceDirectoryIsUntouched()
    {
        var report = ReadOnlyAudit.Run(
            Source, new AuditOptions(Fingerprint: FingerprintMode.Md5, MaxPosters: 0));

        Assert.True(report.Passed, Describe(report));
        Assert.True(report.Diff.IsEmpty);
        Assert.Equal(report.Before.Files.Count, report.After.Files.Count);
        Assert.Equal(report.Before.TotalBytes, report.After.TotalBytes);
    }

    [Fact]
    public void Audit_RunsEveryOperation()
    {
        var report = ReadOnlyAudit.Run(
            Source, new AuditOptions(Fingerprint: FingerprintMode.Metadata, MaxPosters: 0));

        var names = report.Steps.Select(s => s.Name).ToList();

        Assert.Contains("扫描源目录", names);
        Assert.Contains("重建合集", names);
        Assert.Contains("虚拟改名 / 标签 / 隐藏 / 旋转 / 进度", names);
        Assert.Contains("新建收藏夹 / 加入 / 改名 / 移除", names);
        Assert.Contains("重扫后虚拟元数据存活", names);
        Assert.Contains("策略闸门（三档真值表）", names);
        Assert.All(report.Steps, s => Assert.True(s.Ok, $"{s.Name}: {s.Detail}"));
    }

    [Fact]
    public void Audit_MetaWritesActuallyReachTheDatabase()
    {
        // 审计不能是"什么都没做所以什么都没变"。这里确认虚拟写入真的生效了。
        var report = ReadOnlyAudit.Run(
            Source, new AuditOptions(Fingerprint: FingerprintMode.Metadata, MaxPosters: 0));

        var metaStep = report.Steps.Single(s => s.Name.StartsWith("虚拟改名"));
        Assert.Contains("成功 2 条", metaStep.Detail);

        var surviveStep = report.Steps.Single(s => s.Name.StartsWith("重扫后"));
        Assert.Contains("2/2", surviveStep.Detail);
    }

    [Fact]
    public void Audit_LeavesNoTempDirectoriesBehind()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "mrack-audit-*").Length;

        ReadOnlyAudit.Run(Source, new AuditOptions(MaxPosters: 0));

        var after = Directory.GetDirectories(Path.GetTempPath(), "mrack-audit-*").Length;
        Assert.Equal(before, after);
    }

    private static string Describe(AuditReport report) =>
        string.Join(" | ", report.Steps.Select(s => $"{s.Name}:{(s.Ok ? "ok" : "FAIL")} {s.Detail}"));

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
