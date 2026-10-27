using MediaRack.Media;

namespace MediaRack.Server.Hosting;

/// <summary>
/// 本机能力探测。<b>只在启动时跑一次</b>——每次请求都调 <c>ffmpeg -version</c> 会白白多两次进程启动。
/// </summary>
public sealed class Capabilities
{
    private Capabilities(bool ffmpeg, string? error)
    {
        Ffmpeg = ffmpeg;
        Error = error;
    }

    /// <summary>ffmpeg 与 ffprobe 是否都能跑起来。决定 remux 与 single 源探测是否可用。</summary>
    public bool Ffmpeg { get; }

    public string? Error { get; }

    /// <summary>
    /// 硬件编码器。<b>恒为 null</b>：transcode 还没实现，报告一个能用的编码器只会让客户端
    /// 以为可以请求它。实现 transcode 后再在这里探测 h264_nvenc / hevc_qsv 之类。
    /// </summary>
    public string? HardwareEncoder => null;

    public static Capabilities Detect()
    {
        try
        {
            var availability = FfTools.Locate().Check();
            return new Capabilities(availability.Available, availability.Error);
        }
        catch (Exception ex) when (ex is FfException or IOException)
        {
            return new Capabilities(false, ex.Message);
        }
    }

    /// <summary>强制声明"没有 ffmpeg"。测试用：验证降级路径不需要真的卸载 ffmpeg。</summary>
    public static Capabilities Unavailable(string reason = "测试注入：没有 ffmpeg") =>
        new(false, reason);
}
