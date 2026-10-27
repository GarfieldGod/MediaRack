namespace MediaRack.Models;

/// <summary>源的物理布局。</summary>
public enum SourceLayout
{
    /// <summary>B 站缓存：video.m4s + audio.m4s 双流。</summary>
    Dual,

    /// <summary>普通视频文件：单流。</summary>
    Single,
}

/// <summary>源的访问方式。服务端只索引本机目录，<see cref="Http"/> 仅客户端侧有意义。</summary>
public enum SourceKind
{
    Local,
    Http,
}

/// <summary>服务端视野层面的虚拟写权限。磁盘层面永远不可写。</summary>
public enum VirtualWrite
{
    /// <summary>视图不可改，客户端只能存本地覆盖。</summary>
    None,

    /// <summary>只允许改 tags / collection / hidden / play_pos，标题锁定。</summary>
    Whitelist,

    /// <summary>全部虚拟字段可改（仍不改磁盘）。</summary>
    Full,
}

/// <summary>条目健康状态。扫描阶段写入，用于 UI 灰度与排障。</summary>
public enum MediaStatus
{
    Ok,
    Missing,
    Corrupt,
}

/// <summary>流角色。dual 源两条，single 源一条 video。</summary>
public enum TrackRole
{
    Video,
    Audio,
}
