namespace MediaRack.Models;

/// <summary>
/// 一条条目的虚拟元数据。<b>只存在于库里</b>，与源目录上的任何文件无关。
/// <para>
/// 没有对应行时的语义是"全部取源端值"：<see cref="TitleDisplay"/> 为 null 就用 <c>media.title_raw</c>，
/// <see cref="Hidden"/> 为 false、<see cref="Tags"/> 为空、<see cref="Rev"/> 为 0。
/// </para>
/// </summary>
public sealed record MediaMeta
{
    public required string SourceId { get; init; }
    public required string Key { get; init; }

    /// <summary>展示标题覆盖。null = 跟随源端 <c>title_raw</c>。</summary>
    public string? TitleDisplay { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public bool Hidden { get; init; }

    /// <summary>播放旋转角覆盖（0/90/180/270）。用于纠正手机竖拍的躺倒画面。</summary>
    public int Rotation { get; init; }

    public long PlayPosMs { get; init; }
    public int PlayCount { get; init; }
    public long? LastPlayAt { get; init; }

    /// <summary>
    /// 合集归属 override（去源化虚拟键）。null = 跟随扫描投影（目录/UP 主）。
    /// <para>键格式：<c>dir:/相对目录</c>、<c>up:{mid}</c>、<c>custom:{名字}</c>；
    /// 应用时拼上条目当前所在 sourceId，目标合集不存在则回退投影。</para>
    /// </summary>
    public string? Collection { get; init; }

    /// <summary>写入版本。每次成功写入 +1，用于到达序 LWW 与 <c>If-Match</c> 乐观锁。</summary>
    public long Rev { get; init; }

    public long? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }

    /// <summary>是否一条覆盖都没有（等价于"跟随源端"）。</summary>
    public bool IsEmpty =>
        TitleDisplay is null && Tags.Count == 0 && !Hidden && Rotation == 0
        && Collection is null && Rev == 0;
}

/// <summary>
/// 一次虚拟写入的内容。只有被赋值的字段会被改动，其余保持原值。
/// <para><c>null</c> = 不改；<see cref="TitleDisplay"/> 传空串 = 清除覆盖、恢复源端标题。</para>
/// </summary>
public sealed record MetaPatch
{
    public string? TitleDisplay { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? TagsAdd { get; init; }
    public IReadOnlyList<string>? TagsRemove { get; init; }
    public bool ClearTags { get; init; }
    public bool? Hidden { get; init; }
    public int? Rotation { get; init; }
    public long? PlayPosMs { get; init; }

    /// <summary>
    /// 合集归属 override。null = 不改；空串 = 清除（恢复投影）；否则为去源化虚拟键
    /// （<c>dir:/…</c> / <c>up:…</c> / <c>custom:…</c>，格式由 <c>MetaRepository.NormalizeCollection</c> 校验）。
    /// </summary>
    public string? Collection { get; init; }

    /// <summary>记一次播放：<c>play_count + 1</c>、刷新 <c>last_play_at</c>。</summary>
    public bool MarkedPlayed { get; init; }

    /// <summary>补丁是否为空——空补丁不该写库，也不该让 <c>rev</c> 虚增。</summary>
    public bool IsEmpty =>
        TitleDisplay is null && Tags is null && TagsAdd is null && TagsRemove is null &&
        !ClearTags && Hidden is null && Rotation is null && PlayPosMs is null &&
        Collection is null && !MarkedPlayed;
}

/// <summary>虚拟字段。用于按 <see cref="VirtualWrite"/> 判定"这个补丁允不允许写"。</summary>
public enum MetaField
{
    Title,
    Tags,
    Collection,
    Hidden,
    Rotation,
    PlayPos,
}

/// <summary>写入结果。CLI 退出码与 HTTP 状态码都由它映射。</summary>
public enum WriteStatus
{
    Applied,

    /// <summary>源为 <c>virtualWrite = none</c>，或补丁里含该档位不允许的字段。</summary>
    Forbidden,

    /// <summary>源未登记，或条目不存在。</summary>
    NotFound,

    /// <summary><c>If-Match</c> 与服务端 <c>rev</c> 不符。</summary>
    Conflict,
}

/// <summary>写入结果。<c>Value</c> 是写入后的完整元数据，冲突时是被拒绝前服务端的现值。</summary>
public sealed record WriteResult(WriteStatus Status, string? Message = null, MediaMeta? Value = null)
{
    public bool Ok => Status == WriteStatus.Applied;

    public static WriteResult Applied(MediaMeta value) => new(WriteStatus.Applied, null, value);

    public static WriteResult Forbidden(string message) => new(WriteStatus.Forbidden, message);

    public static WriteResult NotFound(string message) => new(WriteStatus.NotFound, message);

    public static WriteResult Conflict(string message, MediaMeta current) =>
        new(WriteStatus.Conflict, message, current);
}

/// <summary>虚拟元数据 + 源端值的合并结果。<b>展示层只读这个</b>，不自己去拼。</summary>
public sealed record ResolvedMedia(
    string SourceId,
    string Key,
    string? TitleRaw,
    MediaMeta Meta)
{
    /// <summary>最终展示标题：本地覆盖 ?? 源端。</summary>
    public string? Title => Meta.TitleDisplay ?? TitleRaw;

    /// <summary>标题是否被改过。</summary>
    public bool TitleOverridden => Meta.TitleDisplay is not null;
}
