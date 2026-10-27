namespace MediaRack.Media;

/// <summary>
/// 中点抽帧。用 ffmpeg 而不是 OpenCV——后者在 Windows 中文路径下会静默失败。
/// <para>
/// 输出只写 <c>meta</c> 目录；抽帧失败返回 false，由调用方降级为占位图，<b>不影响条目入库</b>。
/// </para>
/// </summary>
public sealed class PosterMaker(FfTools tools)
{
    /// <summary>网格缩略图宽度。见 docs/design.md §6。</summary>
    public const int DefaultMaxWidth = 560;

    /// <summary>
    /// 从 <paramref name="videoPath"/> 取中点帧写到 <paramref name="outPath"/>（jpg）。
    /// 竖屏视频按比例缩放，<b>不裁 16:9</b>——B 站大量 2160×3840 竖屏，裁掉会丢主体。
    /// </summary>
    public bool TryMake(
        string videoPath,
        long? durationMs,
        string outPath,
        out string? error,
        int maxWidth = DefaultMaxWidth)
    {
        error = null;

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var seek = SeekSeconds(durationMs);

        // 临时名必须带图像扩展名，否则 ffmpeg 无法从后缀推断输出格式。
        var temp = Path.ChangeExtension(outPath, ".tmp.jpg");

        // scale 表达式：小图不放大，大图缩到目标宽，高度按比例（-2 保证偶数）。
        var scale = $"scale=w='min(iw,{maxWidth})':h=-2";

        string[] args =
        [
            "-hide_banner", "-loglevel", "error",
            "-ss", seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-frames:v", "1",
            "-an", "-sn", "-dn",
            "-vf", scale,
            "-q:v", "4",
            "-y", temp,
        ];

        try
        {
            var run = tools.RunFfmpeg(args, CancellationToken.None);

            if (!run.Ok)
            {
                error = FirstLine(run.StdErr) ?? $"ffmpeg 退出码 {run.ExitCode}";
                SafeDelete(temp);
                return false;
            }

            var info = new FileInfo(temp);
            if (!info.Exists || info.Length == 0)
            {
                error = "ffmpeg 未产出图像（可能整段为黑场或关键帧不可解）";
                SafeDelete(temp);
                return false;
            }

            // 先写 .tmp 再原子替换：中途失败不会留下半张图被当成有效缩略图。
            File.Move(temp, outPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is FfException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            SafeDelete(temp);
            return false;
        }
    }

    /// <summary>取中点。首帧常是黑场或片头 logo，取中点命中正片的概率高得多。</summary>
    private static double SeekSeconds(long? durationMs)
    {
        if (durationMs is not > 0) return 1;

        var mid = durationMs.Value / 2000.0;
        // 留 10% 余量，避免正好落在容器末尾导致取不到帧。
        var limit = durationMs.Value / 1000.0 * 0.9;
        return Math.Max(0, Math.Min(mid, limit));
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主流程。
        }
    }

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var idx = text.IndexOfAny(['\r', '\n']);
        return idx < 0 ? text.Trim() : text[..idx].Trim();
    }
}
