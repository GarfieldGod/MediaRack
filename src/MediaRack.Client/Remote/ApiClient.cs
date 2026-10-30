using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaRack.Client.Remote;

/// <summary>服务端返回了非 2xx。Code 给程序判断，Message 给人看（契约 Error schema，可直接上屏）。</summary>
public sealed class ApiException(int httpStatus, string code, string message)
    : Exception(message)
{
    public int HttpStatus { get; } = httpStatus;
    public string Code { get; } = code;
}

/// <summary>网络层失败（DNS / 超时 / 连接拒绝）。Message 是一句中文结论，原始异常留在 InnerException 给日志。</summary>
public sealed class NetworkException(string reason, Exception? inner = null)
    : Exception(reason, inner);

/// <summary>
/// MediaRack 契约客户端（C# 版）。与 Android 端 core/remote/ApiClient.kt 是同一份契约的两个投影。
///
/// - 鉴权：API 请求带 <c>Authorization: Bearer</c>；流与海报由播放器/图片自己发请求、加不了 header，
///   统一用 <c>?token=</c>（契约三路凭据等价）
/// - url 字段是相对路径时按 base 拼接；流 / 海报地址一律经 <see cref="MediaUrl"/> 补 token
/// </summary>
public sealed class ApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 连接层复用：客户端进程里连接数很少，但短超时探活会频繁建连，共享 handler 避免 socket 抖动。
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 8,
    };

    public string BaseUrl { get; }
    public string? Token { get; }

    private readonly HttpClient _http;
    private readonly bool _disposeHandler;

    public ApiClient(string baseUrl, string? token = null, TimeSpan? timeout = null, bool ownsHandler = false)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Token = string.IsNullOrWhiteSpace(token) ? null : token;
        _disposeHandler = ownsHandler;
        var handler = ownsHandler ? new SocketsHttpHandler { MaxConnectionsPerServer = 8 } : SharedHandler;
        _http = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };
    }

    public void Dispose() => _http.Dispose();

    // ── 请求骨架 ───────────────────────────────────────────────────

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, BaseUrl + (path.StartsWith('/') ? path : "/" + path));
        if (Token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        return req;
    }

    private static HttpRequestMessage JsonBody(HttpRequestMessage req, object? body)
    {
        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: Json);
        }
        else if (req.Method == HttpMethod.Post || req.Method == HttpMethod.Patch)
        {
            // 空 body 的 POST：契约里若干写端点不接受空负载，给个空对象兜底
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NetworkException("连接超时");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException)
        {
            throw new NetworkException(NetworkFailureReason(e), e);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var err = TryParse<ErrorBody>(body);
            throw new ApiException(
                (int)resp.StatusCode,
                err?.Code is { Length: > 0 } c ? c : "http_" + (int)resp.StatusCode,
                err?.Message is { Length: > 0 } m ? m : $"HTTP {(int)resp.StatusCode}");
        }
        return resp;
    }

    private async Task<T> GetJson<T>(string path, CancellationToken ct) where T : class
    {
        using var resp = await SendAsync(Request(HttpMethod.Get, path), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return TryParse<T>(body)
               ?? throw new ApiException(200, "bad_response", "服务端返回内容无法解析");
    }

    private async Task<T> WriteJson<T>(HttpMethod method, string path, object? body, CancellationToken ct) where T : class
    {
        using var resp = await SendAsync(JsonBody(Request(method, path), body), ct).ConfigureAwait(false);
        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return TryParse<T>(s) ?? throw new ApiException(200, "bad_response", "服务端返回内容无法解析");
    }

    private async Task WriteVoid(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var resp = await SendAsync(JsonBody(Request(method, path), body), ct).ConfigureAwait(false);
    }

    private static T? TryParse<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>网络异常 → 一句中文结论（照 Android 端 networkFailureReason，别把英文原文上屏）。</summary>
    private static string NetworkFailureReason(Exception e) => e switch
    {
        TaskCanceledException or OperationCanceledException => "连接超时",
        System.Security.Authentication.AuthenticationException => "证书校验失败",
        HttpRequestException h => h.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "域名解析失败",
            HttpRequestError.ConnectionError => "连接被拒绝",
            _ => "无法建立连接",
        },
        SocketException s => s.SocketErrorCode switch
        {
            SocketError.HostNotFound => "域名解析失败",
            SocketError.ConnectionRefused => "连接被拒绝",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable => "网络不可达",
            SocketError.TimedOut => "连接超时",
            _ => "无法建立连接",
        },
        _ => "无法建立连接",
    };

    // ── 端点 ───────────────────────────────────────────────────────

    /// <summary>免鉴权探活。用于验证可达性。</summary>
    public Task<HealthDto> HealthAsync(CancellationToken ct = default) =>
        GetJson<HealthDto>("/api/v1/health", ct);

    /// <summary>
    /// 免鉴权端点，但<b>带 Bearer 发</b>：令牌填错这里就能看出（authenticated=false），
    /// 不用等到播放时才 401。
    /// </summary>
    public Task<AuthStateDto> AuthStateAsync(CancellationToken ct = default) =>
        GetJson<AuthStateDto>("/api/v1/auth/state", ct);

    private sealed class SourcesResponse { public List<SourceInfoDto> Sources { get; set; } = new(); }

    public async Task<List<SourceInfoDto>> SourcesAsync(CancellationToken ct = default)
    {
        var page = await GetJson<SourcesResponse>("/api/v1/sources", ct).ConfigureAwait(false);
        return page.Sources;
    }

    /// <summary>聚合列表（跨源去重）。collection / favorite 二选一，都是 SQL 层过滤。</summary>
    public Task<MediaPageDto> LibraryAsync(
        int offset = 0,
        int limit = 60,
        string? query = null,
        string? collectionId = null,
        string? favoriteId = null,
        string? sort = null,
        string? seed = null,
        CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder("/api/v1/library?offset=").Append(offset).Append("&limit=").Append(limit);
        if (!string.IsNullOrEmpty(query)) sb.Append("&q=").Append(Enc(query!));
        if (!string.IsNullOrEmpty(collectionId)) sb.Append("&collection=").Append(Enc(collectionId!));
        if (!string.IsNullOrEmpty(favoriteId)) sb.Append("&favorite=").Append(Enc(favoriteId!));
        if (!string.IsNullOrEmpty(sort)) sb.Append("&sort=").Append(Enc(sort!));
        if (!string.IsNullOrEmpty(seed)) sb.Append("&seed=").Append(Enc(seed!));
        return GetJson<MediaPageDto>(sb.ToString(), ct);
    }

    /// <summary>
    /// 单源列表（<c>/api/v1/sources/{id}/library</c>）：不去重、行数即条目数。
    /// 聚合视图会按 key 合并同名条目，看"这个源里到底有什么"要用这条。
    /// </summary>
    public Task<MediaPageDto> SourceLibraryAsync(
        string sourceId,
        int offset = 0,
        int limit = 60,
        string? query = null,
        string? collectionId = null,
        string? favoriteId = null,
        string? sort = null,
        string? seed = null,
        CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder("/api/v1/sources/")
            .Append(Enc(sourceId))
            .Append("/library?offset=").Append(offset).Append("&limit=").Append(limit);
        if (!string.IsNullOrEmpty(query)) sb.Append("&q=").Append(Enc(query!));
        if (!string.IsNullOrEmpty(collectionId)) sb.Append("&collection=").Append(Enc(collectionId!));
        if (!string.IsNullOrEmpty(favoriteId)) sb.Append("&favorite=").Append(Enc(favoriteId!));
        if (!string.IsNullOrEmpty(sort)) sb.Append("&sort=").Append(Enc(sort!));
        if (!string.IsNullOrEmpty(seed)) sb.Append("&seed=").Append(Enc(seed!));
        return GetJson<MediaPageDto>(sb.ToString(), ct);
    }

    /// <summary>合集列表。sourceId 为 null 时返回全部可见源。</summary>
    public Task<List<RemoteCollectionDto>> CollectionsAsync(string? sourceId = null, CancellationToken ct = default)
    {
        var q = string.IsNullOrEmpty(sourceId) ? "" : "?source=" + Enc(sourceId!);
        return GetJson<List<RemoteCollectionDto>>("/api/v1/collections" + q, ct);
    }

    public Task<List<FavoriteDto>> FavoritesAsync(CancellationToken ct = default) =>
        GetJson<List<FavoriteDto>>("/api/v1/favorites", ct);

    public Task<FavoriteDto> CreateFavoriteAsync(string name, CancellationToken ct = default) =>
        WriteJson<FavoriteDto>(HttpMethod.Post, "/api/v1/favorites", new NamePatch { Name = name }, ct);

    public Task<FavoriteDto> RenameFavoriteAsync(string id, string name, CancellationToken ct = default) =>
        WriteJson<FavoriteDto>(HttpMethod.Patch, "/api/v1/favorites/" + Enc(id), new NamePatch { Name = name }, ct);

    public Task DeleteFavoriteAsync(string id, CancellationToken ct = default) =>
        WriteVoid(HttpMethod.Delete, "/api/v1/favorites/" + Enc(id), null, ct);

    public Task<ChangeCountDto> FavoriteItemsAsync(string id, List<ItemRefBody>? add = null,
        List<ItemRefBody>? remove = null, CancellationToken ct = default) =>
        WriteJson<ChangeCountDto>(HttpMethod.Post, "/api/v1/favorites/" + Enc(id) + "/items",
            new FavoriteItemsPatch { Add = add, Remove = remove }, ct);

    public Task<List<RemoteCollectionDto>> FavoriteCollectionsAsync(string id, CancellationToken ct = default) =>
        GetJson<List<RemoteCollectionDto>>("/api/v1/favorites/" + Enc(id) + "/collections", ct);

    /// <summary>
    /// 这一条属于哪些收藏夹。详情侧栏的「收藏到」要把每个夹子的勾选状态一次画全，
    /// 没有这个端点就得为每个收藏夹各翻一遍列表（N 次分页请求）。
    /// </summary>
    public Task<List<string>> FavoritesOfMediaAsync(string source, string key, CancellationToken ct = default) =>
        GetJson<List<string>>($"/api/v1/media/{Enc(source)}/{Enc(key)}/favorites", ct);

    /// <summary>播放进度上报（跨端续播）。游标在服务端，客户端各存一份。</summary>
    public Task ReportProgressAsync(string source, string key, long posMs, bool? completed = null,
        CancellationToken ct = default) =>
        WriteVoid(HttpMethod.Post, $"/api/v1/progress/{Enc(source)}/{Enc(key)}",
            new ProgressPatch { PosMs = posMs, Completed = completed }, ct);

    /// <summary>写入用户覆盖的旋转角（0/90/180/270）。与容器自带 rotation 元数据正交。</summary>
    public Task SetRotationAsync(string source, string key, int rotation, CancellationToken ct = default) =>
        WriteVoid(HttpMethod.Post, $"/api/v1/meta/{Enc(source)}/{Enc(key)}",
            new MediaPatchBody { Rotation = rotation }, ct);

    public Task<MediaDetailDto> MediaDetailAsync(string source, string key, CancellationToken ct = default) =>
        GetJson<MediaDetailDto>($"/api/v1/media/{Enc(source)}/{Enc(key)}", ct);

    private static string Enc(string v) => Uri.EscapeDataString(v);

    // ── 播放地址 ───────────────────────────────────────────────────

    /// <summary>拼绝对流 / 海报地址并补 token（供 LibVLC / 图片加载直接请求）。</summary>
    public string MediaUrl(string pathOrUrl)
    {
        var url = pathOrUrl.StartsWith("http://", StringComparison.Ordinal) ||
                  pathOrUrl.StartsWith("https://", StringComparison.Ordinal)
            ? pathOrUrl
            : BaseUrl + (pathOrUrl.StartsWith('/') ? pathOrUrl : "/" + pathOrUrl);

        if (Token is null) return url;
        return url + (url.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(Token);
    }

    public string ThumbUrl(string source, string key) =>
        MediaUrl($"/media/{Enc(source)}/{Enc(key)}/thumb.jpg");

    /// <summary>单流地址：single + delivery 含 direct。</summary>
    public string? SingleStreamUrl(MediaDetailDto detail)
    {
        if (detail.Layout != "single") return null;
        if (!detail.Delivery.Contains("direct")) return null;
        var track = detail.Tracks.FirstOrDefault(t => t.Role == "video") ?? detail.Tracks.FirstOrDefault();
        return track?.Url is { } u ? MediaUrl(u) : null;
    }

    /// <summary>
    /// 双流地址（video.m4s / audio.m4s 各一条，都补 token）。
    /// 客户端合成方式与 Android 端 Media3 MergingMediaSource 等价（LibVLC 用 :input-slave）。
    /// </summary>
    public (string Video, string Audio)? DualStreamUrls(MediaDetailDto detail)
    {
        if (detail.Layout != "dual") return null;
        if (!detail.Delivery.Contains("direct")) return null;
        var video = detail.Tracks.FirstOrDefault(t => t.Role == "video")?.Url;
        var audio = detail.Tracks.FirstOrDefault(t => t.Role == "audio")?.Url;
        if (video is null || audio is null) return null;
        return (MediaUrl(video), MediaUrl(audio));
    }

    /// <summary>
    /// 直连播不了时的兜底：服务端用 ffmpeg 把双流合成为单个 mp4（含音轨）。
    ///
    /// B 站缓存的 HEVC m4s 常把参数集放在分片里（hvcC 的 numOfArrays=0），
    /// 直连解析会失败；服务端转封装会把参数集写回 hvcC。
    /// </summary>
    public string RemuxUrl(string source, string key) =>
        MediaUrl($"/api/v1/export/{Enc(source)}/{Enc(key)}.mp4");

    /// <summary>
    /// 等转封装就绪：202 = 服务端正在做，200/206 = 可以播。只探一个字节（Range 0-0）。
    /// </summary>
    public async Task<bool> RemuxReadyAsync(string url, int timeoutMs = 90_000, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var code = await ProbeAsync(url, ct).ConfigureAwait(false);
            switch (code)
            {
                case 200 or 206: return true;
                case 202: await Task.Delay(600, ct).ConfigureAwait(false); continue;
                default: return false;
            }
        }
        return false;
    }

    private async Task<int> ProbeAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return (int)resp.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException)
        {
            return 0;
        }
    }

    // ── 静态：地址规范化 / 配对 / 探活（不依赖实例）────────────────

    /// <summary>
    /// 规范化用户输入：去尾斜杠；无 scheme 补 http://。返回 null 表示格式不合法。
    /// base 只允许 host[:port][/路径前缀]，不允许 query / fragment。
    /// </summary>
    public static string? NormalizeBaseUrl(string? input)
    {
        if (input is null) return null;
        var s = input.Trim();
        if (s.Length == 0) return null;
        if (!s.StartsWith("http://", StringComparison.Ordinal) && !s.StartsWith("https://", StringComparison.Ordinal))
            s = "http://" + s;
        s = s.TrimEnd('/');
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        // 带 ?query 或 #fragment 的不是合法 base（令牌另有约定格式）
        if (s.Contains('?') || s.Contains('#')) return null;
        return s;
    }

    /// <summary>源地址（slug）规则：小写字母/数字开头，可含 - _，最长 32 位。</summary>
    public static bool IsValidSlug(string s) =>
        s.Length is > 0 and <= 32 &&
        (char.IsAsciiLetterLower(s[0]) || char.IsAsciiDigit(s[0])) &&
        s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>拼源门户 base URL：<c>http://host:port/s/{slug}</c>（或原样路径前缀）。</summary>
    public static string? BuildSourceBaseUrl(string host, string port, string sourcePath)
    {
        var h = host.Trim();
        var p = port.Trim();
        var s = sourcePath.Trim();
        if (h.Length == 0 || p.Length == 0 || s.Length == 0) return null;
        var path = s.StartsWith('/') ? s.TrimEnd('/') : "/s/" + s;
        return NormalizeBaseUrl($"{h}:{p}{path}");
    }

    /// <summary>
    /// 拆 base URL → (host, port, 源地址路径，带前导 <c>/</c>)。
    /// 没有路径前缀（直接连服务端根、老版本配置）时源地址为空串，调用方据此要求补填。
    /// </summary>
    public static (string Host, string Port, string SourcePath) SplitSourceBaseUrl(string? baseUrl)
    {
        var s = NormalizeBaseUrl(baseUrl);
        if (s is null) return ("", "", "");

        var noScheme = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = noScheme.IndexOf('/');
        var hostPart = slash < 0 ? noScheme : noScheme[..slash];
        var path = slash < 0 ? "" : noScheme[slash..];

        var colon = hostPart.IndexOf(':');
        return colon < 0
            ? (hostPart, "", path)
            : (hostPart[..colon], hostPart[(colon + 1)..], path);
    }

    /// <summary>
    /// 是不是源门户地址（<c>http://主机:端口/s/{源标识}</c>）。
    /// <para>
    /// <b>客户端只认这一种形态。</b>服务端根路径虽然在本机可达，但那是管理页通道，
    /// <c>/api/v1/sources</c> 会把这台机器上的全部源一次列出来——而"一条配置 = 一个源"
    /// 是两端的共同前提，所以任何没有源地址的地址在这里就判为不可用，不做降级。
    /// </para>
    /// </summary>
    public static bool IsSourcePortalUrl(string? baseUrl)
    {
        var (_, _, path) = SplitSourceBaseUrl(baseUrl);
        var rest = path.Trim('/');
        return rest.StartsWith("s/", StringComparison.Ordinal) && rest.Length > 2;
    }
}

/// <summary>
/// 配对码解析。服务端「配对二维码」两种内容：
/// - <c>MR1|{"token":"x","urls":[...]}</c>：一张码打包全部候选地址（Tailscale 优先 / 内网 / 手填公网），
///   客户端并发探活自动选
/// - <c>http://host:port/s/{slug}#token=xxx</c>：单地址码（老版主路径，令牌跟源走）
/// 另兼容 <c>?token=</c> 与无令牌纯地址。
/// </summary>
public static class PairingCode
{
    private const string Prefix = "MR1|";

    public sealed record Payload(string? Token, List<string> Urls);

    public static Payload? Parse(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        if (s.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                var p = JsonSerializer.Deserialize<PayloadDto>(s[Prefix.Length..], ApiClient.Json);
                var urls = (p?.Urls ?? new List<string>())
                    .Select(ApiClient.NormalizeBaseUrl)
                    .Where(u => u is not null)
                    .Select(u => u!)
                    .Distinct()
                    .ToList();
                return urls.Count == 0 ? null : new Payload(p?.Token, urls);
            }
            catch (JsonException) { return null; }
        }

        // http://...#token=xxx
        var hash = s.IndexOf("#token=", StringComparison.Ordinal);
        if (hash >= 0)
        {
            var baseUrl = ApiClient.NormalizeBaseUrl(s[..hash]);
            if (baseUrl is null) return null;
            return new Payload(s[(hash + "#token=".Length)..].Trim(), new List<string> { baseUrl });
        }

        // ...?token=xxx
        var q = s.IndexOf("token=", StringComparison.Ordinal);
        if (q > 0 && s[q - 1] is '?' or '&')
        {
            var baseUrl = ApiClient.NormalizeBaseUrl(s[..(q - 1)]);
            if (baseUrl is null) return null;
            return new Payload(s[(q + "token=".Length)..].Trim(), new List<string> { baseUrl });
        }

        var plain = ApiClient.NormalizeBaseUrl(s);
        return plain is null ? null : new Payload(null, new List<string> { plain });
    }

    private sealed class PayloadDto
    {
        public string? Token { get; set; }
        public List<string> Urls { get; set; } = new();
    }

    /// <summary>
    /// 并发探活候选地址（health 免鉴权），<b>按候选顺序</b>返回第一个可达的——
    /// 顺序即服务端给的优先级（Tailscale 在前）。全部不可达返回 null。
    /// </summary>
    public static async Task<string?> PickReachableBaseUrlAsync(
        IEnumerable<string> urls,
        string? token = null,
        int timeoutMs = 2500,
        CancellationToken ct = default)
    {
        var list = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
        if (list.Count == 0) return null;

        var clients = list.Select(u => new ApiClient(u, token, TimeSpan.FromMilliseconds(timeoutMs), ownsHandler: true)).ToList();
        try
        {
            var tasks = clients.Select(c => TryHealthAsync(c, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (var i = 0; i < results.Length; i++)
            {
                if (results[i]) return list[i];
            }
            return null;
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    private static async Task<bool> TryHealthAsync(ApiClient c, CancellationToken ct)
    {
        try
        {
            await c.HealthAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is ApiException or NetworkException or OperationCanceledException)
        {
            return false;
        }
    }
}
