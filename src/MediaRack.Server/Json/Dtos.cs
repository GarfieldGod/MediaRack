namespace MediaRack.Server.Json;

// 这里的形状严格对应 contracts/openapi.v1.yaml。改这里必须同时改契约，
// 否则 Android 端会照着过期的文档写。

public sealed record SourceDto(
    string Id,
    string Name,
    string Layout,
    string VirtualWrite,
    bool Enabled,
    bool Reachable,
    int ItemCount,
    long? LastScanAt);

public sealed record TrackDto(
    string Role,
    int? Codecid,
    long? Bandwidth,
    long? SizeBytes,
    string? Md5,
    string Url);

public sealed record MediaSummaryDto(
    string Key,
    string SourceId,
    string Layout,
    string Title,
    string? UpName,
    long? DurationMs,
    int? Width,
    int? Height,
    long? SizeBytes,
    int? TagCode,
    string PosterUrl,
    bool HasDanmaku,
    bool Hidden,
    long PlayPosMs,
    /// <summary>用户覆盖的旋转角（0/90/180/270）。容器自带的 rotation 已被解码器自动应用，这里只是"额外再转"。</summary>
    int Rotation,
    string Status,
    IReadOnlyList<string>? Sources,
    string? Preferred,
    string CollectionId,
    string CollectionName,
    string CollectionKind);

public sealed record MediaDetailDto(
    string Key,
    string SourceId,
    string Layout,
    string Title,
    string? UpName,
    long? DurationMs,
    int? Width,
    int? Height,
    long? SizeBytes,
    int? TagCode,
    string PosterUrl,
    bool HasDanmaku,
    bool Hidden,
    long PlayPosMs,
    string Status,
    string? RelPath,
    string? RelDir,
    string? Codec,
    IReadOnlyList<TrackDto> Tracks,
    IReadOnlyList<string> Delivery,
    MediaMetaDto Meta,
    string CollectionId,
    string CollectionName,
    string CollectionKind);

public sealed record MediaPageDto(int Total, IReadOnlyList<MediaSummaryDto> Items, int? NextOffset);

public sealed record MediaMetaDto(
    string? TitleDisplay,
    IReadOnlyList<string> Tags,
    bool Hidden,
    int Rotation,
    long PlayPosMs,
    int PlayCount,
    long? LastPlayAt,
    long Rev,
    string? Collection);

/// <summary>请求体。只有出现过的字段才会被改——JSON 里没有的字段保持 null。</summary>
public sealed record MediaPatchDto(
    string? TitleDisplay,
    IReadOnlyList<string>? Tags,
    bool? Hidden,
    int? Rotation,
    IReadOnlyList<string>? Collections,
    string? Collection);

/// <summary>批量元数据操作：同一个源的一批条目应用同一组覆盖。字段 null = 不动。</summary>
public sealed record MediaBatchPatchDto(
    string Source,
    IReadOnlyList<string> Keys,
    int? Rotation,
    bool? Hidden,
    IReadOnlyList<string>? Tags,
    string? Collection);

public sealed record ProgressPatchDto(long PosMs, bool? Completed);

/// <summary>
/// 结构合集（v4）：平铺、源级。<see cref="Kind"/> = folder（single 目录）/ up（dual UP 主）；
/// <see cref="Path"/> 是相对源的目录或 up_mid，<see cref="Name"/> 只是展示名（目录末段）。
/// </summary>
public sealed record CollectionDto(
    string Id,
    string Name,
    string SourceId,
    string Kind,
    string Path,
    int ItemCount);

/// <summary>收藏夹（用户层）。</summary>
public sealed record FavoriteDto(string Id, string Name, int ItemCount, int CollectionCount);

public sealed record FavoritePatchDto(string Name);

public sealed record FavoriteItemsPatchDto(
    IReadOnlyList<ItemRefDto>? Add,
    IReadOnlyList<ItemRefDto>? Remove);

public sealed record FavoriteCollectionsPatchDto(
    IReadOnlyList<string>? Add,
    IReadOnlyList<string>? Remove);

public sealed record CollectionItemsPatchDto(
    IReadOnlyList<ItemRefDto>? Add,
    IReadOnlyList<ItemRefDto>? Remove);

public sealed record ItemRefDto(string Source, string Key);

/// <summary>
/// 扫描请求。带 <see cref="Root"/> 就是"登记一个源并立刻扫它"——Web 页的"加源"走这条路，
/// 因为扫描本来就是登记源的动作，不需要两个端点做同一件事。
/// </summary>
public sealed record ScanRequestDto(
    string? SourceId,
    string? Root,
    string? Name,
    string? Layout,
    string? Verify);

/// <summary>
/// 扫描任务。带进度字段是为了让前端能画进度条——以前只有 <see cref="State"/>，
/// 一个几万条的源扫起来几分钟，页面上一个字都没有，用户只能干等。
/// </summary>
/// <param name="Stage">见 <see cref="MediaRack.Scan.ScanStage"/>。</param>
/// <param name="Done">当前阶段已完成数。</param>
/// <param name="Total">当前阶段总数；<b>0 = 未知</b>，前端显示不确定进度条。</param>
/// <param name="Current">正在处理的相对路径。</param>
public sealed record ScanJobDto(
    string JobId,
    string State,
    string? SourceId,
    string? Message,
    int Written,
    int MarkedMissing,
    long? StartedAt,
    long? FinishedAt,
    string Stage = MediaRack.Scan.ScanStage.Queued,
    int Done = 0,
    int Total = 0,
    string? Current = null);

public sealed record SourceHealthDto(string Id, bool Reachable, int ItemCount);

public sealed record CapabilitiesDto(bool Ffmpeg, string? HardwareEncoder);

public sealed record HealthDto(
    string Status,
    string Version,
    int SchemaVersion,
    IReadOnlyList<SourceHealthDto> Sources,
    CapabilitiesDto Capabilities,
    // 本机库目录。未认证时为 null——探活不需要知道磁盘结构。
    string? MetaRoot,
    bool AuthRequired);

/// <summary>鉴权状态。<see cref="AuthRequired"/> 是服务端配置，<see cref="Authenticated"/> 是本次请求的结论。</summary>
public sealed record AuthStateDto(bool AuthRequired, bool Authenticated);

public sealed record AuthRequestDto(string? Token);

/// <summary>配对信息（主路径见 docs/design.md §10）。Hosts 是本机可达的私网地址，Token 为 null 表示服务公开。</summary>
public sealed record PairingDto(IReadOnlyList<string> Hosts, string? Token);

public sealed record ErrorDto(string Code, string Message);

public sealed record JobAcceptedDto(string JobId);

// ── 管理页（仅本机）──────────────────────────────────────────
// 这一组端点的共同前提：请求来自根路径。源门户（/s/{slug}）下一律 403，
// 否则任何拿到单个源令牌的人都能顺着管理端点把别的源（含令牌）读出来。

/// <summary>本机的一个可访问地址。<see cref="Kind"/> 是 lan / virtual / other，前端据此分组。</summary>
public sealed record AdminHostDto(string Address, string Kind, int Port);

/// <summary>源名下的一个内容文件夹。layout 是 auto / single / dual。</summary>
public sealed record AdminFolderDto(long Id, string Root, string Layout, bool Reachable);

/// <summary>
/// 管理页里的一个源。<see cref="Token"/> 明文下发是<b>有意</b>的：
/// 这个端点只在本机可用，而用户需要复制它去配客户端。令牌打码在这里没有意义。
/// <para>
/// <see cref="Root"/> / <see cref="Layout"/> 是首文件夹的遗留投影，UI 一律读 <see cref="Folders"/>。
/// </para>
/// <para>
/// <see cref="ItemCount"/> 只数<b>真实存在</b>的条目（status=ok），
/// <see cref="MissingCount"/> 单独给缺失数——missing 行是恢复锚点，留在库里但不算"库里有"。
/// </para>
/// </summary>
public sealed record AdminSourceDto(
    string Id,
    string Slug,
    string Name,
    string? Token,
    string Root,
    string Layout,
    string VirtualWrite,
    int ItemCount,
    int MissingCount,
    long TotalBytes,
    long? LastScanAt,
    bool Reachable,
    IReadOnlyList<AdminFolderDto> Folders);

public sealed record AdminServiceDto(
    string Name,
    int Port,
    string? PublicUrl,
    IReadOnlyList<AdminHostDto> Hosts,
    IReadOnlyList<AdminSourceDto> Sources);

/// <summary>
/// 新建源。<see cref="Slug"/> 与 <see cref="Token"/> 都可省略：省略就生成。
/// <b>不含文件夹</b>——文件夹在源详情的「文件夹来源」里添加（一个源可以挂多个，布局跟文件夹走）。
/// </summary>
public sealed record CreateSourceDto(string? Name, string? Slug, string? Token);

/// <summary>给源添加文件夹。<see cref="Layout"/> 是 auto / single / dual，省略 = auto。</summary>
public sealed record CreateFolderDto(string? Root, string? Layout);

/// <summary>改文件夹布局（auto / single / dual）。</summary>
public sealed record UpdateFolderDto(string Layout);

/// <summary>改源信息。全部可空，只改给出的字段。</summary>
public sealed record UpdateSourceDto(string? Name, string? Slug, string? Token, string? VirtualWrite);

/// <summary>改服务器展示信息。目前只有公网地址——它<b>没法自动探测</b>（NAT 后面看不到出口 IP）。</summary>
public sealed record UpdateServiceDto(string? PublicUrl);

public sealed record CreateSourceResultDto(AdminSourceDto Source, string? JobId);

/// <summary>
/// 删除源的结果。默认只删"位置层"（源登记 + 文件夹 + 条目 + 合集投影），
/// 档案与收藏留着等文件回来认领；<see cref="WithArchive"/> 为 true 时连档案一起删。
/// </summary>
/// <param name="CancelledJobs">被打断的该源扫描任务数（防止删完又被扫描写回来）。</param>
public sealed record DeleteSourceResultDto(
    string SourceId,
    int MediaRemoved,
    int FoldersRemoved,
    int CollectionsRemoved,
    int MetaRemoved,
    int FavoriteItemsRemoved,
    int MetaKept,
    int FavoriteItemsKept,
    int FingerprintsRemoved,
    int CancelledJobs,
    bool WithArchive);
