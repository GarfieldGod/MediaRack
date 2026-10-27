namespace MediaRack.Models;

/// <summary>
/// 待生成缩略图的条目快照。<see cref="MediaRack.Media.PosterGenerator"/> 只依赖这个，
/// 因此不必为了抽帧把整个 <see cref="MediaEntry"/> 读出来。
/// </summary>
/// <param name="Key">内容键。</param>
/// <param name="RelPath">相对源根目录的路径。</param>
/// <param name="Layout">源布局，决定如何定位主视频文件。</param>
/// <param name="TagCode">dual 源的清晰度档位。</param>
/// <param name="PosterRel">已有的封面路径（相对 meta 根）；空表示还没生成。</param>
/// <param name="DurationMs">用于取中点帧。</param>
public sealed record PosterCandidate(
    string Key,
    string RelPath,
    SourceLayout Layout,
    int? TagCode,
    string? PosterRel,
    long? DurationMs);
