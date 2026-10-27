using System.Net;
using MediaRack.Models;
using MediaRack.Server.Json;
using MediaRack.Store;

namespace MediaRack.Server.Hosting;

/// <summary>
/// 源门户：<c>/s/{slug}/…</c> → "只看得见一个源"的常规请求。
/// <para>
/// 为什么要这一层，而不是每个端点都加一个 <c>source</c> 参数：库浏览、封面、视频流、导出
/// 一共十几个端点，全部改签名既容易漏，也会让"某个端点忘了校验"变成静默的越权。
/// 这里一次性把请求属于谁钉死，下游只认 <see cref="ServiceScope"/>。
/// </para>
/// <para>
/// 它<b>不是</b>第二个 Kestrel 实例：同一端口、同一管线、同一套端点，
/// 只是把"当前是谁"从"按端口查 config.json"换成"按 URL 段查库"。所以新增一个源
/// 不需要重启——库里多一行就立刻可用。
/// </para>
/// </summary>
internal static class SourcePortal
{
    public const string Prefix = "/s/";

    /// <summary>配了令牌也仍然放行的路径。<b>改这里等于改安全边界</b>，别顺手加。</summary>
    public static bool IsPublicPath(string path) =>
        path is "/" or "/index.html" or "/app.js" or "/style.css" or "/favicon.ico" ||
        string.Equals(path, "/api/v1/health", StringComparison.Ordinal) ||
        path.StartsWith("/api/v1/auth/", StringComparison.Ordinal);

    public static async Task InvokeAsync(
        HttpContext context, RequestDelegate next, LibraryAccess access, ServiceScope scope)
    {
        var path = context.Request.Path.Value ?? "/";

        if (path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            await InvokePortalAsync(context, next, path, access, scope);
            return;
        }

        // 根路径 = 管理页，只给本机。这条是"不写源地址就进不来"的落点：
        // 从局域网/公网过来的请求没有别的大门可走。
        if (!IsLoopback(context.Connection.RemoteIpAddress))
        {
            await Deny(context, StatusCodes.Status403Forbidden, "loopback_only",
                "管理页只能在本机打开；客户端请使用 /s/{源地址}");
            return;
        }

        // 这里<b>不</b> Bind：根路径仍归 config.json 里的端口服务管，
        // 由下一个中间件按端口 Bind 并校验服务级密码。在这里代劳会把鉴权整段跳过。
        await next(context);
    }

    private static async Task InvokePortalAsync(
        HttpContext context, RequestDelegate next, string path, LibraryAccess access, ServiceScope scope)
    {
        var rest = path[Prefix.Length..];
        var slash = rest.IndexOf('/');
        var slug = slash < 0 ? rest : rest[..slash];
        var tail = slash < 0 ? string.Empty : rest[slash..];

        if (slug.Length == 0)
        {
            await Deny(context, StatusCodes.Status404NotFound, "source_not_found", "缺源地址");
            return;
        }

        // 端点全是 /api/v1/... 这种绝对路径，页面里的相对资源靠尾斜杠才能正确解析。
        if (tail.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            context.Response.Headers.Location = $"{Prefix}{slug}/";
            return;
        }

        var source = access.With(db => new SourceRepository(db).GetBySlug(slug));

        // 源不存在与"还没分配令牌"同样处理：两者都不该进得来。
        if (source is null || string.IsNullOrEmpty(source.Token))
        {
            await Deny(context, StatusCodes.Status404NotFound, "source_not_found", $"源未登记：{slug}");
            return;
        }

        var profile = new ServiceProfile
        {
            Name = source.Name,
            Port = context.Connection.LocalPort,
            Password = source.Token,
            // 访问等级直接映射到既有的 virtual_write：none 就是"只读浏览"，
            // 不给它单独造一个字段，否则同一个意思会有两套开关。
            Writable = source.VirtualWrite != VirtualWrite.None,
            Sources = [source.Id],
            Slug = source.Slug,
        };

        // 非公开路径必须带令牌。401 而不是 404：页面要靠它判断"该弹登录框了"。
        // （slug 不是秘密——用户要口头告诉别人"我挂在 movie 上"；真正的边界是令牌。）
        if (!IsPublicPath(tail) && !Auth.IsAuthorized(context.Request, profile.Password, profile.CookieName))
        {
            await Deny(context, StatusCodes.Status401Unauthorized, "unauthorized", "需要令牌");
            return;
        }

        scope.Bind(profile);

        // 剥前缀交给下游：端点看到的仍是 /api/v1/library，一行都不用改。
        // PathBase 也要设，否则以后引入 Url/LinkGenerator 生成的链接会掉前缀。
        context.Request.PathBase = Prefix + slug;
        context.Request.Path = tail;

        await next(context);
    }

    /// <summary>
    /// 环回判定。IPv4-mapped IPv6（<c>::ffff:127.0.0.1</c>）要映射回 v4 再判，
    /// <see cref="IPAddress.IsLoopback"/> 对它返回 false。
    /// <para>
    /// 远端地址为 null 时按本机处理：进程内调用（WebApplicationFactory/TestServer）没有
    /// 远端地址，若判成"非本机"会让所有测试直接 403。
    /// </para>
    /// </summary>
    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return true;
        if (IPAddress.IsLoopback(address)) return true;
        return address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }

    private static Task Deny(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ErrorDto(code, message));
    }
}
