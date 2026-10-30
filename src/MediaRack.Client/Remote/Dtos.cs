namespace MediaRack.Client.Remote;

/// <summary>
/// 契约 DTO（contracts/openapi.v1.yaml v1）。
///
/// 字段与契约一一对应，命名走 CamelCase 策略自动映射（PlayPosMs ↔ playPosMs），
/// 服务端将来加字段不会炸客户端；缺字段按默认值处理。
/// 与 Android 端 core/remote/Dtos.kt 是同一份契约的两个语言投影，改一边要改另一边。
/// </summary>

/// <summary>/api/v1/health。</summary>
public sealed class HealthDto
{
    public string Status { get; set; } = "ok";
    public string? Version { get; set; }
    public int? SchemaVersion { get; set; }
    public List<HealthSourceDto> Sources { get; set; } = new();
}

public sealed class HealthSourceDto
{
    public string Id { get; set; } = "";
    public bool Reachable { get; set; } = true;
    public int ItemCount { get; set; }
}

/// <summary>/api/v1/auth/state。authRequired=false 表示服务端没设密码。</summary>
public sealed class AuthStateDto
{
    public bool AuthRequired { get; set; }
    public bool Authenticated { get; set; }
}

public sealed class SourceInfoDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Layout { get; set; } = "single";
    public string? VirtualWrite { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Reachable { get; set; } = true;
    public int ItemCount { get; set; }
    public long? LastScanAt { get; set; }
}

public sealed class MediaPageDto
{
    public int Total { get; set; }
    public List<MediaSummaryDto> Items { get; set; } = new();
    public int? NextOffset { get; set; }
}

public sealed class MediaSummaryDto
{
    public string Key { get; set; } = "";
    /// <summary>单源视图=实际源；聚合视图=preferred 源。</summary>
    public string SourceId { get; set; } = "";
    /// <summary>dual / single。</summary>
    public string Layout { get; set; } = "single";
    public string Title { get; set; } = "";
    public string? UpName { get; set; }
    public long DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public int? TagCode { get; set; }
    public string? PosterUrl { get; set; }
    public bool HasDanmaku { get; set; }
    public bool Hidden { get; set; }
    public long PlayPosMs { get; set; }
    /// <summary>用户覆盖的旋转角（0/90/180/270）。容器自带 rotation 解码器已自动应用，这是额外再转。</summary>
    public int Rotation { get; set; }
    /// <summary>ok / missing / corrupt。</summary>
    public string Status { get; set; } = "ok";
    public List<string>? Sources { get; set; }
    public string? Preferred { get; set; }
    public string CollectionId { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public string CollectionKind { get; set; } = "";
}

public sealed class MediaDetailDto
{
    public string Key { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Layout { get; set; } = "single";
    public string Title { get; set; } = "";
    public string? UpName { get; set; }
    public long DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public int? TagCode { get; set; }
    public string? PosterUrl { get; set; }
    public bool HasDanmaku { get; set; }
    public bool Hidden { get; set; }
    public long PlayPosMs { get; set; }
    public string Status { get; set; } = "ok";
    public List<string>? Sources { get; set; }
    public string? Preferred { get; set; }
    public string? RelPath { get; set; }
    public string? RelDir { get; set; }
    public string? Codec { get; set; }
    public List<TrackDto> Tracks { get; set; } = new();
    /// <summary>当前设备实际可用的交付模式：direct / remux / transcode。</summary>
    public List<string> Delivery { get; set; } = new();
    public MediaMetaDto? Meta { get; set; }
    public string CollectionId { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public string CollectionKind { get; set; } = "";
}

public sealed class TrackDto
{
    /// <summary>video / audio。</summary>
    public string Role { get; set; } = "";
    public int? Codecid { get; set; }
    public long? Bandwidth { get; set; }
    public long? SizeBytes { get; set; }
    public string? Md5 { get; set; }
    /// <summary>客户端可直接使用的地址（相对路径；single=/file，dual=/video.m4s 或 /audio.m4s）。</summary>
    public string? Url { get; set; }
}

public sealed class MediaMetaDto
{
    public string? TitleDisplay { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool Hidden { get; set; }
    public int Rotation { get; set; }
    public long PlayPosMs { get; set; }
    public int PlayCount { get; set; }

    /// <summary>从没播过的条目这里是 <c>null</c>（不是 0）。可空类型非得这么写，否则整条响应解析失败。</summary>
    public long? LastPlayAt { get; set; }

    public int Rev { get; set; }
}

/// <summary>结构合集（v4）：源级、平铺、只读。kind=folder 目录合集 / kind=up UP 主合集。</summary>
public sealed class RemoteCollectionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Kind { get; set; } = "folder";
    public string Path { get; set; } = "";
    public int ItemCount { get; set; }
}

/// <summary>收藏夹（用户层，跨端）。</summary>
public sealed class FavoriteDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int ItemCount { get; set; }
    public int CollectionCount { get; set; }
}

public sealed class ChangeCountDto
{
    public int Added { get; set; }
    public int Removed { get; set; }
}

// ── 请求体 ──────────────────────────────────────────────────────────

public sealed class ItemRefBody
{
    public string Source { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed class FavoriteItemsPatch
{
    public List<ItemRefBody>? Add { get; set; }
    public List<ItemRefBody>? Remove { get; set; }
}

public sealed class NamePatch
{
    public string Name { get; set; } = "";
}

public sealed class ProgressPatch
{
    public long PosMs { get; set; }
    public bool? Completed { get; set; }
}

/// <summary>POST /api/v1/meta/{source}/{key}。语义：JSON 里为 null = 不改这个字段。</summary>
public sealed class MediaPatchBody
{
    public int? Rotation { get; set; }
    public List<string>? Collections { get; set; }
}

/// <summary>契约错误体（契约 Error schema），可直接上屏。</summary>
internal sealed class ErrorBody
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}
