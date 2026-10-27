using System.Diagnostics;
using MediaRack.Audit;
using MediaRack.Models;
using MediaRack.Scan;
using MediaRack.Store;

namespace MediaRack.Cli;

/// <summary>
/// <c>mrack verify-readonly</c> — 用可执行的方式证明"源目录永不被写"。
/// <para>
/// 这条命令存在的意义：只读性不能靠"我没写那段代码"来保证。任何一次重构都可能引入写操作，
/// 所以把它做成一条可以随时重跑、结论只有"通过/不通过"的命令。
/// </para>
/// </summary>
internal static class VerifyReadonlyCommand
{
    public const string HelpText =
        """
        mrack verify-readonly — 源目录只读性的机械证明

        用法：
          mrack verify-readonly --source <id>
          mrack verify-readonly --root <目录> [--layout auto|dual|single] [--source <id>]

        选项：
          --source <id>      已登记的源；给 --root 时只借用它的 id
          --root <目录>      临时指定目录（不必先扫描入库）
          --layout <布局>    仅 --root 时有效，默认 auto
          --verify <模式>    dual 源的校验强度 none | sample | full（默认 sample）
          --hash            快照追加全文件 md5（慢，但结论最强）
          --max-posters <n>  审计中最多抽几张帧（默认 20）

        做法：
          先给源目录拍全量指纹快照 → 跑一整套真实操作（扫描、抽帧、改名、标签、
          隐藏、旋转、进度、新建收藏夹、重扫、三档写权限闸门）→ 再拍一次快照 →
          要求 diff 为空。任何一项非空都判不通过。

        退出码：0 通过 / 1 不通过（或执行失败） / 2 参数错误
        """;

    public static int Run(string[] args, string metaTarget)
    {
        var options = Options.Parse(args);
        if (options.Flag("help"))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var source = ResolveSource(options, metaTarget);

        var mode = options.Flag("hash") ? FingerprintMode.Md5 : FingerprintMode.Metadata;
        var auditOptions = new AuditOptions(
            Verify: ParseVerify(options.Get("verify")),
            Fingerprint: mode,
            MaxPosters: ParseInt(options.Get("max-posters"), 20));

        var sw = Stopwatch.StartNew();
        var report = ReadOnlyAudit.Run(source, auditOptions);
        sw.Stop();

        Print(report, mode, sw.Elapsed);
        return report.Passed ? 0 : 1;
    }

    private static SourceConfig ResolveSource(Options options, string metaTarget)
    {
        var sourceId = options.Get("source");
        var rootOption = options.Get("root");

        if (string.IsNullOrWhiteSpace(sourceId) && string.IsNullOrWhiteSpace(rootOption))
        {
            throw new CliException("需要 --source <id> 或 --root <目录>");
        }

        if (!string.IsNullOrWhiteSpace(rootOption))
        {
            var root = Path.GetFullPath(rootOption);
            if (!Directory.Exists(root)) throw new CliException($"目录不存在：{root}");

            var id = string.IsNullOrWhiteSpace(sourceId)
                ? Path.GetFileName(root.TrimEnd('\\', '/'))
                : sourceId;

            return new SourceConfig
            {
                Id = id,
                Name = id,
                Root = root,
                Layout = ParseLayout(options.Get("layout"), root),
            };
        }

        using var db = LibraryDatabase.Open(metaTarget, create: false);
        return new SourceRepository(db).Get(sourceId!)
               ?? throw new CliException($"源不存在：{sourceId}");
    }

    private static void Print(AuditReport report, FingerprintMode mode, TimeSpan elapsed)
    {
        var source = report.Source;

        Console.WriteLine("只读性审计 — 源目录在整套操作前后必须完全一致");
        Console.WriteLine();
        Console.WriteLine($"源        {source.Id}  ({(source.Layout == SourceLayout.Dual ? "dual" : "single")})");
        Console.WriteLine($"目录      {source.Root}");
        Console.WriteLine($"指纹      {FingerprintName(mode)}");
        Console.WriteLine();

        Console.WriteLine("执行的操作");
        foreach (var step in report.Steps)
        {
            Console.WriteLine($"  {(step.Ok ? "✓" : "✗")} {step.Name}");
            Console.WriteLine($"      {step.Detail}");
        }

        foreach (var note in report.Notes)
        {
            Console.WriteLine($"  · {note}");
        }

        Console.WriteLine();
        Console.WriteLine("快照");
        Console.WriteLine($"  文件    {report.Before.Files.Count} → {report.After.Files.Count}");
        Console.WriteLine($"  目录    {report.Before.Directories.Count} → {report.After.Directories.Count}");
        Console.WriteLine($"  字节    {report.Before.TotalBytes} → {report.After.TotalBytes}");

        var diff = report.Diff;
        Console.WriteLine();
        Console.WriteLine("差异");
        Console.WriteLine(
            $"  新增 {diff.Added.Count}  删除 {diff.Removed.Count}  修改 {diff.Modified.Count}  " +
            $"新增目录 {diff.AddedDirectories.Count}  删除目录 {diff.RemovedDirectories.Count}");

        PrintDiff(diff);

        Console.WriteLine();
        if (report.Passed)
        {
            Console.WriteLine("结论      ✅ 通过：源目录未被改动（diff 为空）");
        }
        else
        {
            var failedSteps = report.Steps.Where(s => !s.Ok).Select(s => s.Name).ToList();
            Console.WriteLine("结论      ❌ 不通过");
            if (!diff.IsEmpty) Console.WriteLine($"          源目录被改动 {diff.TotalChanges} 处");
            if (failedSteps.Count > 0) Console.WriteLine($"          失败步骤：{string.Join("、", failedSteps)}");
        }

        Console.WriteLine();
        Console.WriteLine($"耗时      {elapsed.TotalMilliseconds:F0} ms");
    }

    private static void PrintDiff(SnapshotDiff diff)
    {
        const int limit = 20;
        var lines = new List<string>();

        lines.AddRange(diff.Added.Select(f => $"+ 文件 {f.RelPath}（{f.Size} 字节）"));
        lines.AddRange(diff.Removed.Select(f => $"- 文件 {f.RelPath}（{f.Size} 字节）"));
        lines.AddRange(diff.Modified.Select(m =>
            $"~ 文件 {m.Before.RelPath}（{Describe(m.Before)} → {Describe(m.After)}）"));
        lines.AddRange(diff.AddedDirectories.Select(d => $"+ 目录 {d}"));
        lines.AddRange(diff.RemovedDirectories.Select(d => $"- 目录 {d}"));

        foreach (var line in lines.Take(limit))
        {
            Console.WriteLine($"  {line}");
        }

        if (lines.Count > limit)
        {
            Console.WriteLine($"  …… 另有 {lines.Count - limit} 处未显示");
        }
    }

    private static string Describe(FileFingerprint f) =>
        f.Hash is null ? $"{f.Size}B @{f.MTimeTicks}" : $"{f.Size}B {f.Hash[..8]}";

    private static string FingerprintName(FingerprintMode mode) =>
        mode == FingerprintMode.Md5 ? "path + size + mtime + md5" : "path + size + mtime";

    private static SourceLayout ParseLayout(string? value, string root) => value?.ToLowerInvariant() switch
    {
        null or "" or "auto" => LayoutDetector.Detect(root),
        "dual" => SourceLayout.Dual,
        "single" => SourceLayout.Single,
        _ => throw new CliException($"--layout 只接受 auto / dual / single，收到 {value}"),
    };

    private static VerifyMode ParseVerify(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => VerifyMode.Sample,
        "none" => VerifyMode.None,
        "sample" => VerifyMode.Sample,
        "full" => VerifyMode.Full,
        _ => throw new CliException($"--verify 只接受 none / sample / full，收到 {value}"),
    };

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var n) && n > 0 ? n : fallback;
}
