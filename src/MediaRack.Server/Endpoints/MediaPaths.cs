using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Server.Hosting;

namespace MediaRack.Server.Endpoints;

/// <summary>
/// 把库里的相对路径落到磁盘绝对路径。全服务端只有这一处做这件事，
/// 于是就只有一个地方需要保证"路径不会跑出源根目录"。
/// </summary>
internal static class MediaPaths
{
    public static string? Video(SourceConfig source, MediaEntry entry) =>
        MediaLocator.FindPrimaryVideo(source, entry.RelPath, entry.Layout, entry.TagCode);

    public static string? Audio(SourceConfig source, MediaEntry entry)
    {
        if (entry.Layout != SourceLayout.Dual) return null;

        var video = Video(source, entry);
        if (video is null) return null;

        var audio = Path.Combine(Path.GetDirectoryName(video)!, "audio.m4s");
        return File.Exists(audio) ? audio : null;
    }

    public static string? SingleFile(SourceConfig source, MediaEntry entry)
    {
        if (entry.Layout != SourceLayout.Single) return null;

        return MediaLocator.RootCandidates(source)
            .Select(root => MediaLocator.Combine(root, entry.RelPath))
            .FirstOrDefault(File.Exists);
    }

    public static string? Poster(ServerOptions options, MediaEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PosterRel)) return null;

        var path = Path.Combine(options.MetaRoot, entry.PosterRel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 按扩展名给 content type。播放器认 MIME（Media3 尤其），给
    /// <c>application/octet-stream</c> 会让它去猜甚至在探测阶段就放弃。
    /// </summary>
    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m4s" or ".mp4" or ".m4v" => "video/mp4",
        ".m4a" => "audio/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".flv" => "video/x-flv",
        ".ts" => "video/mp2t",
        _ => "application/octet-stream",
    };

    /// <summary>单流文件按角色给 MIME（<c>.m4s</c> 既可能是视频也可能是音频）。</summary>
    public static string ContentTypeForTrack(string path, TrackRole role)
    {
        if (role == TrackRole.Audio) return "audio/mp4";
        return ContentTypeFor(path);
    }
}
