using System.Diagnostics;

namespace MediaRack.Media;

/// <summary>
/// ffmpeg / ffprobe 的定位与调用。
/// <para>
/// 只用于<b>读取</b>源文件（探测、抽帧）和写入 <c>meta</c> 目录，从不修改源目录。
/// </para>
/// <para>
/// 定位顺序：环境变量 → PATH。允许显式指定，便于测试与"绿色版"部署。
/// </para>
/// </summary>
public sealed class FfTools
{
    public const string FfmpegEnvVar = "MEDIARACK_FFMPEG";
    public const string FfprobeEnvVar = "MEDIARACK_FFPROBE";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PosterTimeout = TimeSpan.FromSeconds(120);

    private FfTools(string ffmpeg, string ffprobe)
    {
        Ffmpeg = ffmpeg;
        Ffprobe = ffprobe;
    }

    public string Ffmpeg { get; }
    public string Ffprobe { get; }

    /// <summary>按环境变量 / PATH 自动定位。</summary>
    public static FfTools Locate() => new(
        ResolveTool(Environment.GetEnvironmentVariable(FfmpegEnvVar), "ffmpeg"),
        ResolveTool(Environment.GetEnvironmentVariable(FfprobeEnvVar), "ffprobe"));

    /// <summary>显式指定两个可执行文件（测试用）。</summary>
    public static FfTools Explicit(string ffmpeg, string ffprobe) => new(ffmpeg, ffprobe);

    /// <summary>探测可用性。返回版本首行，失败时给出可执行的修复提示。</summary>
    public FfAvailability Check()
    {
        var ffmpeg = TryVersion(Ffmpeg);
        var ffprobe = TryVersion(Ffprobe);

        if (ffmpeg is null || ffprobe is null)
        {
            var missing = ffmpeg is null ? "ffmpeg" : "ffprobe";
            return new FfAvailability(
                false, ffmpeg, ffprobe,
                $"找不到 {missing}。请安装 ffmpeg 并加入 PATH，或设置环境变量 {FfmpegEnvVar} / {FfprobeEnvVar} 指向可执行文件。");
        }

        return new FfAvailability(true, ffmpeg, ffprobe, null);
    }

    internal FfResult RunFfprobe(IReadOnlyList<string> args, CancellationToken ct) =>
        Run(Ffprobe, args, ProbeTimeout, ct);

    internal FfResult RunFfmpeg(IReadOnlyList<string> args, CancellationToken ct) =>
        Run(Ffmpeg, args, PosterTimeout, ct);

    private static FfResult Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new FfException($"无法启动 {exe}：{ex.Message}", ex);
        }

        // 必须并发读两个管道，否则子进程写满缓冲区后会永久阻塞。
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new FfException($"{Path.GetFileName(exe)} 超时（{timeout.TotalSeconds:F0}s）。");
        }

        return new FfResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已经退出或没有权限，忽略。
        }
    }

    private static string? TryVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var line = process.StandardOutput.ReadLine();
            if (!process.WaitForExit(5000))
            {
                TryKill(process);
                return null;
            }

            return process.ExitCode == 0 ? line : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>环境变量优先；否则在 PATH 中找（Windows 补 .exe）。</summary>
    private static string ResolveTool(string? fromEnv, string name)
    {
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : [name];
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in names)
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), candidate);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException)
                {
                    // PATH 里可能有非法路径，跳过。
                }
            }
        }

        // 没找到也返回裸名：让真正调用时报出可读的错误，而不是在这里静默失败。
        return name;
    }
}

/// <summary>ffmpeg / ffprobe 的可用性。</summary>
/// <param name="Available">两者是否都能跑起来。</param>
/// <param name="FfmpegVersion">ffmpeg <c>-version</c> 首行。</param>
/// <param name="FfprobeVersion">ffprobe <c>-version</c> 首行。</param>
/// <param name="Error">不可用时的修复提示。</param>
public sealed record FfAvailability(bool Available, string? FfmpegVersion, string? FfprobeVersion, string? Error);

/// <summary>一次外部进程调用的结果。</summary>
public sealed record FfResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>ffmpeg 调用失败。</summary>
public sealed class FfException(string message, Exception? inner = null) : Exception(message, inner);
