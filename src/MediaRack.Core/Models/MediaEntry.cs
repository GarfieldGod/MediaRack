namespace MediaRack.Models;

/// <summary>库中的一条媒体。<c>(SourceId, Key)</c> 是主键——同一 key 可在多个源各有一条。</summary>
public sealed class MediaEntry
{
    public required string SourceId { get; init; }
    public required string Key { get; init; }

    /// <summary>相对源根目录的路径。绝不存绝对路径。</summary>
    public required string RelPath { get; init; }

    /// <summary>物理目录（single 源即合集名）。</summary>
    public string? RelDir { get; init; }

    public required SourceLayout Layout { get; init; }

    public string? TitleRaw { get; set; }
    public string? UpName { get; set; }
    public string? UpMid { get; set; }

    /// <summary>所属合集（v4 排他归属：一个条目恰好一个）。由 CollectionRebuilder 回填。</summary>
    public string? CollectionId { get; set; }

    public long? DurationMs { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Codec { get; set; }

    /// <summary>dual 源的清晰度档位（目录名，如 120 / 80 / 64）。</summary>
    public int? TagCode { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary>
    /// 文件修改时间（Unix 毫秒，UTC）。single 源扫描时记录，下次扫描用来判变：
    /// mtime + size 没变就跳过 ffprobe 沿用库值。dual 条目恒为 null。
    /// </summary>
    public long? FileMtime { get; set; }

    /// <summary>封面/抽帧统一入口，相对 meta_root。空表示尚未生成。</summary>
    public string? PosterRel { get; set; }

    public bool HasDanmaku { get; set; }

    public MediaStatus Status { get; set; } = MediaStatus.Ok;

    public long? ScannedAt { get; set; }

    public IReadOnlyList<MediaTrack> Tracks { get; set; } = [];
}
