namespace MediaRack.Client.ViewModels;

/// <summary>
/// 连播队列里的一条。只带"打开它需要的最小信息"——海报、时长、续播进度都在列表那边，
/// 播放页不持有卡片对象（换页/清列表时不会把整片 UI 树的引用一起拽住）。
/// </summary>
public sealed record PlayQueueItem(string SourceId, string Key, string Title, string Subtitle);

/// <summary>
/// 播放模式，与 APK 端 <c>PlayMode</c> 三态一致（点一次换下一个，循环）：
/// <list type="bullet">
/// <item><b>播完暂停</b>：停在最后一帧，不自动往下走（默认，最不容易"手一滑就丢进度"）</item>
/// <item><b>循环播放</b>：本条播完从头再来</item>
/// <item><b>自动切集</b>：播完翻到下一条，**最后一条则停**（不回头、不循环整个列表）</item>
/// </list>
/// 用枚举而不是两个布尔量：三态互斥，用 bool 会造出"既循环又自动切集"这种没人知道该信谁的状态。
/// </summary>
public enum PlayMode
{
    PauseAtEnd,
    LoopOne,
    AutoNext,
}

/// <summary>播完之后要做什么。<see cref="Stop"/> 也会用于"自动切集但已经是最后一条"。</summary>
public enum EndAction
{
    Stop,
    Replay,
    Next,
}

public static class PlayModes
{
    public static string Label(this PlayMode mode) => mode switch
    {
        PlayMode.LoopOne => "循环播放",
        PlayMode.AutoNext => "自动切集",
        _ => "播完暂停",
    };

    /// <summary>循环切换：播完暂停 → 循环播放 → 自动切集 → 播完暂停。</summary>
    public static PlayMode Next(this PlayMode mode) => (PlayMode)(((int)mode + 1) % 3);

    /// <summary>
    /// 播完后干什么。抽成纯函数是为了能单测——这三种模式的差别全在这一行里，
    /// 混进 <c>OnEnded</c> 的副作用（上报、改状态）之后就没人验得动了。
    /// </summary>
    public static EndAction DecideEnd(this PlayMode mode, bool hasNext) => mode switch
    {
        PlayMode.LoopOne => EndAction.Replay,
        PlayMode.AutoNext when hasNext => EndAction.Next,
        _ => EndAction.Stop,
    };
}
