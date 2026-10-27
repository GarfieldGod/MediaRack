namespace MediaRack.Server.Hosting;

/// <summary>
/// 一个"对外服务"——同一台机器、同一个进程、同一个库，但对局域网/公网呈现出不同的一副面孔。
/// <para>
/// 存在的唯一理由是<b>权限与可见范围不同</b>：客厅电视那个端口全公开、只挂"电影"源；
/// 书桌那个端口要密码、能看到全部源。<b>不是</b>为了隔离数据——库只有一个，
/// 所以在这里改名，换个端口看也是改过的名字。
/// </para>
/// <para>
/// 多个 <see cref="ServiceProfile"/> 共用一个进程：Kestrel 按 <see cref="Port"/> 分别监听，
/// 中间件用 <c>Connection.LocalPort</c> 判定当前请求落在哪个服务上。
/// </para>
/// </summary>
public sealed record ServiceProfile
{
    /// <summary>显示名，只用于日志和托盘菜单（"客厅"、"全部"）。不参与路由。</summary>
    public required string Name { get; init; }

    /// <summary>监听端口。同一个进程内必须唯一，重复会直接启动失败。</summary>
    public required int Port { get; init; }

    /// <summary>
    /// 源门户的 URL 段（<c>/s/{Slug}</c>）。非空表示"这是一个按源划出来的门户"，
    /// 它的令牌、可写、可见源全部只属于那一个源，与 config.json 里的端口服务无关。
    /// <para>
    /// 用同一个 <see cref="ServiceProfile"/> 承载两种身份，是为了让下游
    /// （<c>CanSee</c> / <c>VisibleSourceIds</c> / <c>Writable</c> / 鉴权中间件）一行都不用改。
    /// </para>
    /// </summary>
    public string? Slug { get; init; }

    /// <summary>
    /// 访问密码。为 null 表示完全公开。
    /// <para>
    /// 它就是旧的 <c>--token</c>：Bearer / cookie / <c>?token=</c> 三路都认这个值。
    /// 多服务下每个端口各校验各的，所以<b>解锁 8080 不会顺带解锁 8081</b>。
    /// </para>
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// 是否允许写（改名/标签/收藏/进度/扫描加源）。默认允许，保持单服务时的既有行为。
    /// <para>
    /// 公开到公网的服务应当设成 <c>false</c>：否则任何一个能打开页面的设备都能改你的库。
    /// 这是"完全公开"这个词唯一需要小心的地方。
    /// </para>
    /// </summary>
    public bool Writable { get; init; } = true;

    /// <summary>
    /// 可见源白名单（源 id）。<b>空数组 = 可见全部源</b>，而不是"什么都看不见"。
    /// <para>
    /// 这是本文件唯一一个"空集合有特殊含义"的字段，注释写在这里是为了避免后来者踩坑。
    /// </para>
    /// </summary>
    public string[] Sources { get; init; } = [];

    /// <summary>不需要密码。</summary>
    public bool IsPublic => string.IsNullOrEmpty(Password);

    /// <summary>没有源白名单 → 可见全部。</summary>
    public bool SeesAllSources => Sources.Length == 0;

    /// <summary>
    /// 该服务对应的 cookie 名。<b>必须带端口</b>：cookie 的匹配规则里<b>不含端口</b>，
    /// 同一个 host 的 8080 与 8081 共享 cookie 池，用同一个名字就会出现
    /// "登录了公开服务、加密服务也跟着放行"。
    /// <para>
    /// 源门户下改按 slug 区分：同一端口上开了十个源，cookie 不能混成一份。
    /// </para>
    /// </summary>
    public string CookieName => Slug is null ? Auth.CookieNameFor(Port) : Auth.CookieNameForSource(Slug);

    /// <summary>
    /// cookie 生效路径。门户下收窄到 <c>/s/{slug}</c>——浏览器按路径自动分拣，
    /// 同一浏览器开两个源不会把 A 的令牌送到 B 去。比"给每个源起个不同的 cookie 名"更可靠：
    /// 名字方案依赖每个端点都记得用对名字，路径方案由浏览器保证。
    /// </summary>
    public string CookiePath => Slug is null ? "/" : $"/s/{Slug}";

    /// <summary>这个服务能否看到某个源。</summary>
    public bool CanSee(string sourceId) =>
        SeesAllSources || Array.Exists(Sources, s => string.Equals(s, sourceId, StringComparison.Ordinal));
}
