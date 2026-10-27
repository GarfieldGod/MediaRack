using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using MediaRack.Client.Tools;

namespace MediaRack.Client.Playback;

/// <summary>
/// libvlc native 的<b>进程级单例</b>。
///
/// 为什么要单例而不是每个播放器一份：<see cref="LibVLC"/> 实例本身很轻，真正重的是
/// <see cref="MediaPlayer"/>（一个播放管线）。整条链路只需要一个播放器，换条目只是换 Media，
/// 所以引擎初始化一次、生命周期跟进程走。
///
/// 为什么失败要"软着陆"：libvlc 是 300 MB 的 native 包，缺文件 / 架构不匹配（ARM 机器装 x64）
/// 都会在 <see cref="Core.Initialize"/> 抛异常。那时浏览功能（海报墙、搜索、收藏夹）完全不受影响，
/// 只有播放不可用——所以这里吞掉异常、把原因留给界面显示，绝不往上冒。
/// </summary>
public static class PlaybackEngine
{
    private static readonly object Gate = new();

    private static LibVLC? _instance;
    private static bool _tried;
    private static string? _error;
    private static string? _nativeDir;

    /// <summary>初始化失败的原因；null = 没失败（或还没试）。</summary>
    public static string? Error => _error;

    /// <summary>实际加载的 native 目录（诊断用，null 表示交给 LibVLCSharp 自己探测）。</summary>
    public static string? NativeDirectory => _nativeDir;

    /// <summary>默认引擎参数。双流用 <c>:input-slave</c> 在 Media 上挂，不走这里。</summary>
    public static readonly string[] DefaultArgs =
    [
        "--no-osd",                 // 不显示 "音量 80%" 这类临时提示，控制条自己有
        "--no-video-title-show",    // 起播别在画面中央盖文件名
        "--no-snapshot-preview",
        "--no-stats",
    ];

    /// <summary>
    /// 取引擎。第一次调用时的 <paramref name="args"/> 生效，之后忽略
    /// （所以无头自检必须先于播放器创建，才能用 <c>--vout=dummy</c> 换掉真实输出）。
    /// </summary>
    public static LibVLC? Get(string[]? args = null)
    {
        lock (Gate)
        {
            if (_tried) return _instance;
            _tried = true;

            try
            {
                var dir = FindNativeDirectory();
                if (dir is not null) Core.Initialize(dir);
                else Core.Initialize();

                _nativeDir = dir;
                _instance = new LibVLC(args ?? DefaultArgs);
                ClientLog.Write($"libvlc 就绪：{_instance.Version}" + (dir is null ? "（系统默认路径）" : $"（{dir}）"));
            }
            catch (Exception e)
            {
                // 常见原因：发布包漏了 libvlc 目录、机器是 ARM 但装的是 x64 包、Linux 没装 libvlc
                _error = e.Message;
                _instance = null;
                ClientLog.Write("libvlc 初始化失败：" + e);
            }

            return _instance;
        }
    }

    /// <summary>
    /// 找 native 目录。NuGet 包把它们铺到 <c>{输出目录}/libvlc/{rid}/</c>，
    /// 单文件发布时不会被塞进 bundle，正好还在 exe 旁边——所以按 rid 找得到就显式指路。
    /// 找不到就交给 LibVLCSharp 默认探测（macOS 的 Homebrew、Linux 的 /usr/lib 走这条路）。
    /// </summary>
    private static string? FindNativeDirectory()
    {
        var env = Environment.GetEnvironmentVariable("LIBVLC_PATH");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        var rid = CurrentRid();
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "libvlc", rid),
            Path.Combine(AppContext.BaseDirectory, "libvlc"),
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "libvlc*").Any()) return c;
        }
        return null;
    }

    private static string CurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows()) return "win-" + arch;
        if (OperatingSystem.IsMacOS()) return "osx-" + arch;
        return "linux-" + arch;
    }
}
