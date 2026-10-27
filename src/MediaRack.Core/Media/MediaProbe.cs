using System.Globalization;
using System.Text.Json;

namespace MediaRack.Media;

/// <summary>从媒体文件读到的技术元数据。全部字段可空——读不出来不该让条目无法入库。</summary>
/// <param name="DurationMs">时长（毫秒）。</param>
/// <param name="Width">显示宽度（已按旋转角交换，竖屏即窄边）。</param>
/// <param name="Height">显示高度。</param>
/// <param name="Rotation">容器声明的旋转角，0/90/180/270。</param>
/// <param name="Codec">归一化后的编码名：avc / hevc / av1 / vp9 / 其它原名。</param>
/// <param name="HasAudio">是否含音频轨（single 源也常带音频，只是同一文件内）。</param>
public sealed record ProbeResult(
    long? DurationMs,
    int? Width,
    int? Height,
    int Rotation,
    string? Codec,
    bool HasAudio);

/// <summary>媒体探测。抽出来是为了让扫描逻辑能脱离 ffprobe 做单元测试。</summary>
public interface IMediaProbe
{
    /// <summary>探测失败返回 false，<paramref name="error"/> 给出可读原因；不抛异常。</summary>
    bool TryProbe(string path, out ProbeResult result, out string? error);
}

/// <summary>什么都不做的探测：实现"没有 ffmpeg 也能建库"，只是没有时长/分辨率。</summary>
public sealed class NullProbe : IMediaProbe
{
    public bool TryProbe(string path, out ProbeResult result, out string? error)
    {
        result = new ProbeResult(null, null, null, 0, null, false);
        error = "未启用媒体探测（缺少 ffprobe）";
        return false;
    }
}

/// <summary>基于 <c>ffprobe -of json</c> 的探测实现。</summary>
public sealed class FfProbe(FfTools tools) : IMediaProbe
{
    public bool TryProbe(string path, out ProbeResult result, out string? error)
    {
        result = new ProbeResult(null, null, null, 0, null, false);
        error = null;

        FfResult run;
        try
        {
            run = tools.RunFfprobe(
                ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
                CancellationToken.None);
        }
        catch (FfException ex)
        {
            error = ex.Message;
            return false;
        }

        if (!run.Ok || string.IsNullOrWhiteSpace(run.StdOut))
        {
            error = FirstLine(run.StdErr) ?? "ffprobe 未输出任何内容";
            return false;
        }

        try
        {
            result = Parse(run.StdOut);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"ffprobe 输出无法解析：{ex.Message}";
            return false;
        }
    }

    /// <summary>解析 ffprobe JSON。字段类型不定（duration 有时是字符串），因此逐项容错。</summary>
    internal static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double? durationSec = null;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var fd))
        {
            durationSec = ToDouble(fd);
        }

        int? width = null;
        int? height = null;
        var rotation = 0;
        string? codec = null;
        var hasAudio = false;
        double? fallbackDuration = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;

                if (type == "audio")
                {
                    hasAudio = true;
                    continue;
                }

                if (type != "video") continue;

                // 封面图（mp3/flac 内嵌、mkv attachment）也是 video 流，但不是正片。
                if (IsAttachedPicture(s)) continue;

                width ??= ToInt(s, "width");
                height ??= ToInt(s, "height");
                codec ??= NormalizeCodec(s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null);
                fallbackDuration ??= ToDouble(s, "duration");
                rotation = rotation == 0 ? ReadRotation(s) : rotation;
            }
        }

        var durationMs = durationSec is > 0
            ? (long?)(durationSec * 1000)
            : fallbackDuration is > 0 ? (long?)(fallbackDuration * 1000) : null;

        // 容器可能存"横向 3840×2160 + rotation=90"，用户看到的是竖屏 2160×3840。
        // 缩略图按显示尺寸判断比例，所以这里就交换，避免后续每处都要再判断一次。
        if (rotation is 90 or 270 && width is not null && height is not null)
        {
            (width, height) = (height, width);
        }

        return new ProbeResult(durationMs, width, height, rotation, codec, hasAudio);
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disp) &&
        disp.TryGetProperty("attached_pic", out var pic) &&
        ToInt(pic) == 1;

    private static int ReadRotation(JsonElement stream)
    {
        // 新版本 ffmpeg：display matrix 在 side_data_list 里，逆时针为正。
        if (stream.TryGetProperty("side_data_list", out var sides) && sides.ValueKind == JsonValueKind.Array)
        {
            foreach (var side in sides.EnumerateArray())
            {
                if (!side.TryGetProperty("rotation", out var r)) continue;
                var v = ToDouble(r);
                if (v is null) continue;
                return NormalizeAngle((int)Math.Round(-v.Value)); // 取显示方向
            }
        }

        // 老容器：tags.rotate
        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate))
        {
            var v = ToDouble(rotate);
            if (v is not null) return NormalizeAngle((int)Math.Round(v.Value));
        }

        return 0;
    }

    /// <summary>把任意角度折算到 0/90/180/270。</summary>
    private static int NormalizeAngle(int degrees)
    {
        var a = ((degrees % 360) + 360) % 360;
        return a switch
        {
            0 => 0,
            90 => 90,
            180 => 180,
            270 => 270,
            _ => 0,
        };
    }

    /// <summary>把 ffprobe 的 codec_name 归一到 dual 源已有的词汇（avc 而非 h264）。</summary>
    private static string? NormalizeCodec(string? name) => name?.ToLowerInvariant() switch
    {
        null or "" => null,
        "h264" => "avc",
        var other => other,
    };

    private static int? ToInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) ? ToInt(e) : null;

    private static int? ToInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var i) => i,
        JsonValueKind.String when int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    private static double? ToDouble(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var e) ? ToDouble(e) : null;

    private static double? ToDouble(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetDouble(out var d) => d,
        JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var idx = text.IndexOfAny(['\r', '\n']);
        return idx < 0 ? text.Trim() : text[..idx].Trim();
    }
}
