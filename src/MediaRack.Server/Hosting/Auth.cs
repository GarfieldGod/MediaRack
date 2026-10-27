using System.Security.Cryptography;
using System.Text;

namespace MediaRack.Server.Hosting;

/// <summary>
/// 凭据提取。三条路都得认，因为发起请求的客户端能力不一样：
///
/// <list type="table">
/// <item><term>Bearer</term><description>Android / curl / 任何写代码的客户端</description></item>
/// <item><term>Cookie</term><description>浏览器。<c>&lt;img src&gt;</c> 与 <c>&lt;video src&gt;</c> 没有地方加自定义头，
/// 缩略图和播放流只能靠 cookie，否则配了令牌就全变 401。</description></item>
/// <item><term>query <c>?token=</c></term><description>VLC / 投屏 / 电视盒子。这类客户端只能给一个 URL。</description></item>
/// </list>
///
/// query 这一路会出现在播放器的历史记录里，属于已知代价——它的使用场景就是"我把链接贴进 VLC"，
/// 没有比 URL 更窄的信道可用。
/// </summary>
public static class Auth
{
    /// <summary>
    /// 单服务时代的 cookie 名。多服务下每个端口有自己的名字（见 <see cref="CookieNameFor"/>），
    /// 这里保留是为了让旧会话在升级后仍然有效。
    /// </summary>
    public const string CookieName = "mrack_token";

    public const string QueryName = "token";

    /// <summary>
    /// 某个端口的 cookie 名。
    /// <para>
    /// <b>为什么必须带端口</b>：cookie 的匹配只看 domain + path，<b>端口不参与匹配</b>。
    /// 若两个服务共用 <c>mrack_token</c>，那么在 8080 登录（或干脆 8080 本来就不要密码）之后，
    /// 浏览器会把同一个 cookie 一并送到 8081 —— 等于加密服务被公开服务顺带解锁了。
    /// </para>
    /// </summary>
    public static string CookieNameFor(int port) => $"{CookieName}_s{port}";

    /// <summary>
    /// 某个源门户的 cookie 名。slug 只会是 <c>[a-z0-9-_]</c>（见 <c>SourceIdentity.IsValidSlug</c>），
    /// 都是合法的 cookie 名字符，不需要再转义。
    /// </summary>
    public static string CookieNameForSource(string slug) => $"{CookieName}_r{slug}";

    /// <summary>请求是否已带正确凭据。三路任意一条命中即通过。</summary>
    public static bool IsAuthorized(HttpRequest request, string expected) =>
        IsAuthorized(request, expected, CookieName);

    /// <summary>指定 cookie 名的版本（多服务模式用这个）。</summary>
    public static bool IsAuthorized(HttpRequest request, string expected, string cookieName)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            Matches(header["Bearer ".Length..].Trim(), expected))
        {
            return true;
        }

        // 新旧两个名字都认：旧名只可能是升级前留下的，认它是为了别让人重新登录一次。
        if (request.Cookies.TryGetValue(cookieName, out var cookie) && Matches(cookie, expected))
        {
            return true;
        }

        if (!string.Equals(cookieName, CookieName, StringComparison.Ordinal) &&
            request.Cookies.TryGetValue(CookieName, out var legacy) && Matches(legacy, expected))
        {
            return true;
        }

        if (request.Query.TryGetValue(QueryName, out var query) && query.Count > 0 &&
            Matches(query[0] ?? string.Empty, expected))
        {
            return true;
        }

        return false;
    }

    /// <summary>常数时间比较。普通 <c>==</c> 会在第一个不同字节处返回，可以按字节猜令牌。</summary>
    public static bool Matches(string? candidate, string expected)
    {
        if (candidate is null) return false;

        var a = Encoding.UTF8.GetBytes(candidate);
        var b = Encoding.UTF8.GetBytes(expected);

        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
