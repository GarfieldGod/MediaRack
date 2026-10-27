namespace MediaRack.Server.Hosting;

/// <summary>
/// 当前请求落在哪个服务上。由中间件在管道最前面按 <c>Connection.LocalPort</c> 绑定，
/// 之后所有端点和鉴权都从这里读结论。
/// <para>
/// 注册为 <b>Scoped</b>：一个请求一份，互不串味。它是唯一知道"这个厕所归谁管"的地方——
/// 端点不该自己去读端口，否则同一个判断会在十几个地方各写一遍。
/// </para>
/// </summary>
public sealed class ServiceScope
{
    private ServiceProfile? _profile;

    /// <summary>当前服务。中间件没跑就访问会抛异常，那是管线配置错误，不该静默兜底。</summary>
    public ServiceProfile Profile =>
        _profile ?? throw new InvalidOperationException(
            "ServiceScope 未绑定服务：请确认 ServiceRouter 中间件注册在端点之前。");

    public bool Bound => _profile is not null;

    internal void Bind(ServiceProfile profile) => _profile = profile;

    /// <summary>
    /// 能否看到某个源。
    /// <para>
    /// <b>不可见的源一律返回 404，不返回 403。</b>403 等于承认"这个源存在，只是不给你看"，
    /// 外界能把你的库结构摸出来；404 什么也不泄露。
    /// </para>
    /// </summary>
    public bool CanSee(string sourceId) => Profile.CanSee(sourceId);

    /// <summary>
    /// 可见源白名单，直接喂给 <c>SummaryQuery.SourceIds</c>。
    /// null 表示不限制（能看全部），不是"什么都看不到"。
    /// </summary>
    public IReadOnlyCollection<string>? VisibleSourceIds =>
        Profile.SeesAllSources ? null : Profile.Sources;

    /// <summary>本服务是否允许写。只读服务上的写操作返回 403。</summary>
    public bool Writable => Profile.Writable;
}
