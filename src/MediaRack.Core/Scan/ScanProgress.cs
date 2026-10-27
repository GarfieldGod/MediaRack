namespace MediaRack.Scan;

/// <summary>
/// 扫描的阶段。用字符串常量而不是枚举：它要原样进 JSON，
/// 前端不必再维护一份"数字 → 文案"的映射表（那张表迟早会和后端脱节）。
/// </summary>
public static class ScanStage
{
    /// <summary>任务刚建好，还没轮到它跑。</summary>
    public const string Queued = "queued";

    /// <summary>正在读盘解析条目。最耗时的阶段，进度条主要靠它推进。</summary>
    public const string Scan = "scan";

    /// <summary>条目写库 + 标记缺失。</summary>
    public const string Write = "write";

    /// <summary>重建合集投影（整库幂等重建）。</summary>
    public const string Collections = "collections";

    /// <summary>抽帧生成缩略图。</summary>
    public const string Posters = "posters";

    public const string Done = "done";

    public const string Failed = "failed";
}

/// <summary>一次进度上报。</summary>
/// <param name="Stage">见 <see cref="ScanStage"/>。</param>
/// <param name="Done">本阶段已完成数。</param>
/// <param name="Total">本阶段总数；<b>0 表示还不知道</b>（前端显示不确定进度条，不要算出 100%）。</param>
/// <param name="Current">正在处理的相对路径，只用于展示，可为 null。</param>
public sealed record ScanProgress(string Stage, int Done, int Total, string? Current = null)
{
    /// <summary>0~100。<see cref="Total"/> 未知时恒为 0——"0/0"不该渲染成满格。</summary>
    public int Percent => Total <= 0 ? 0 : Math.Clamp((int)(Done * 100L / Total), 0, 100);
}

/// <summary>
/// 把 <see cref="IProgress{T}"/> 接到一个委托上。
/// <para>
/// 不用 <see cref="Progress{T}"/>：它会抓 <see cref="SynchronizationContext"/> 把回调
/// 丢回"原线程"，而扫描跑在后台 Task 里，那个原线程是线程池线程——语义既不可靠也没必要。
/// 这里直接在调用线程同步执行，任务对象的字段被别的线程轮询时读到的是最新值。
/// </para>
/// </summary>
public sealed class DelegateProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
