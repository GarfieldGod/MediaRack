namespace MediaRack.Models;

/// <summary>一条轨的完整性契约。字段直接对应 B 站缓存 <c>index.json</c> 的记录。</summary>
public sealed class MediaTrack
{
    public required TrackRole Role { get; init; }

    /// <summary>B 站 codecid：12 = HEVC，7 = H.264，13 = AV1。</summary>
    public int? CodecId { get; init; }

    /// <summary>码率（bps），用于选路与弱网判定。</summary>
    public long? Bandwidth { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>小写十六进制 md5。与 SizeBytes 一起构成"缓存完整"的判据。</summary>
    public string? Md5 { get; init; }
}
