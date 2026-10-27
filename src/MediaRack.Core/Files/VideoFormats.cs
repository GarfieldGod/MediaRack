namespace MediaRack.Files;

/// <summary>
/// 单流源认得的视频扩展名。
/// <para>
/// 刻意不含 <c>.m4s</c>——它是 dual 源的轨，不是独立可播放文件。
/// </para>
/// </summary>
public static class VideoFormats
{
    private static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".flv", ".wmv",
        ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".ogv", ".rmvb", ".rm",
    };

    /// <summary>扩展名是否属于视频。传入文件名或路径均可。</summary>
    public static bool IsVideo(string pathOrName) => Set.Contains(Path.GetExtension(pathOrName));

    public static IReadOnlyCollection<string> All => Set;
}
