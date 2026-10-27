using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MediaRack.Server.Tests;

/// <summary>
/// 令牌鉴权的三路凭据。
/// <para>
/// 这三路不是"多支持几种写法"，而是被客户端能力逼出来的：
/// 浏览器给 <c>&lt;img&gt;</c> / <c>&lt;video&gt;</c> 加不了自定义头，只能靠 cookie；
/// VLC 只能吃一个 URL，只能靠 query。少一路就有一类客户端完全用不了。
/// </para>
/// </summary>
public sealed class AuthTests : IClassFixture<AuthTests.TokenFactory>
{
    public const string Token = "test-token-123";

    private readonly TokenFactory _factory;

    /// <summary>带 cookie 容器的客户端，用来验证"登录一次之后浏览器自己带票"。</summary>
    private readonly HttpClient _browser;

    /// <summary>完全不带任何凭据的客户端。</summary>
    private readonly HttpClient _anonymous;

    public AuthTests(TokenFactory factory)
    {
        _factory = factory;
        _browser = factory.CreateClient();
        _anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
    }

    public sealed class TokenFactory : MediaRackFactory
    {
        public TokenFactory() : base(Token) { }
    }

    // ── 免鉴权路径 ──────────────────────────────────────────

    [Fact]
    public async Task 健康检查免鉴权_但未认证时不吐磁盘路径()
    {
        using var doc = await GetJson(_anonymous, "/api/v1/health");

        Assert.True(doc.RootElement.GetProperty("authRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("metaRoot").ValueKind);
        // 死活状态要照常给：探活不该因为没令牌就一无所知。
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 登录状态端点免鉴权_报告未认证()
    {
        using var doc = await GetJson(_anonymous, "/api/v1/auth/state");

        Assert.True(doc.RootElement.GetProperty("authRequired").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/style.css")]
    public async Task 静态页免鉴权_否则页面都打不开(string path)
    {
        using var response = await _anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── 拒绝 ────────────────────────────────────────────────

    [Fact]
    public async Task 没有凭据时受保护端点一律401()
    {
        foreach (var path in new[]
                 {
                     "/api/v1/sources",
                     "/api/v1/library",
                     $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s",
                     $"/poster/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}.jpg",
                 })
        {
            using var response = await _anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task 令牌错误时401且不下发cookie()
    {
        using var response = await _anonymous.PostAsJsonAsync(
            "/api/v1/auth/login", new { token = "not-the-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // ── 三路凭据 ────────────────────────────────────────────

    [Fact]
    public async Task Cookie路径_登录后封面与视频流都能取()
    {
        await Login(_browser);

        // 这两个是浏览器自己发起的请求，没有回头加头的机会，只能靠 cookie。
        using var poster = await _browser.GetAsync(
            $"/poster/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}.jpg");
        Assert.Equal(HttpStatusCode.OK, poster.StatusCode);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);

        using var video = await _browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, video.StatusCode);
    }

    [Fact]
    public async Task Bearer路径_够给写代码的客户端用()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sources");
        request.Headers.Add("Authorization", $"Bearer {Token}");

        using var response = await _anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Query路径_够给只能吃URL的播放器用()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s?token={Token}");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);

        using var response = await _anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
    }

    [Fact]
    public async Task Query里的令牌不对时401()
    {
        using var response = await _anonymous.GetAsync(
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s?token=wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 登录成功后健康检查才吐库目录()
    {
        await Login(_browser);

        using var doc = await GetJson(_browser, "/api/v1/health");
        var metaRoot = doc.RootElement.GetProperty("metaRoot").GetString();

        Assert.False(string.IsNullOrWhiteSpace(metaRoot));
        Assert.Equal(_factory.MetaRoot, metaRoot);
    }

    // ── 未配令牌时要保持原样 ────────────────────────────────

    [Fact]
    public async Task 不配令牌时一切照旧_页面不需要登录()
    {
        // 另起一个不带 token 的工厂：默认的局域网自用场景不能被"加了鉴权"影响。
        using var plain = new MediaRackFactory();
        using var client = plain.CreateClient();

        using var state = await GetJson(client, "/api/v1/auth/state");
        Assert.False(state.RootElement.GetProperty("authRequired").GetBoolean());
        Assert.True(state.RootElement.GetProperty("authenticated").GetBoolean());

        using var sources = await client.GetAsync("/api/v1/sources");
        Assert.Equal(HttpStatusCode.OK, sources.StatusCode);
    }

    // ── 辅助 ────────────────────────────────────────────────

    private async Task Login(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { token = Token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Set-Cookie"), "登录必须下发 cookie");
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
