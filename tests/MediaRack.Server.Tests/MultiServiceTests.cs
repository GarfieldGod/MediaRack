using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MediaRack.Server.Json;
using Xunit;

namespace MediaRack.Server.Tests;

/// <summary>
/// 服务隔离：同一个库、同一个进程、两个"对外服务"，看到的与能改的都不一样。
/// <para>
/// 这里只启一个服务实例来测——WebApplicationFactory 没有真实端口，<c>Connection.LocalPort</c> 恒为 0，
/// 中间件会回落到"第一个服务"。所以"端口 → 服务"的映射逻辑单独在 <see cref="ServiceCatalogTests"/>
/// 里测，两者合起来才覆盖完整链路。
/// </para>
/// </summary>
public sealed class MultiServiceTests : IClassFixture<MultiServiceTests.RestrictedFactory>
{
    private const string Password = "s3cret";

    /// <summary>受限服务：只能看见 single 源、要密码、只读。</summary>
    public sealed class RestrictedFactory : MediaRackFactory
    {
        public RestrictedFactory() : base(null, """
            [{"name":"家庭","port":8081,"password":"s3cret","writable":false,"sources":["single"]}]
            """)
        { }
    }

    private readonly RestrictedFactory _factory;

    public MultiServiceTests(RestrictedFactory factory) => _factory = factory;

    private HttpClient Anonymous => _factory.CreateClient();

    private HttpClient Authorized
    {
        get
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Password);
            return client;
        }
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        var response = await Anonymous.GetAsync("/api/v1/sources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("unauthorized", error!.Code);
    }

    [Fact]
    public async Task Only_the_visible_source_is_listed()
    {
        var payload = await Authorized.GetFromJsonAsync<SourceListPayload>("/api/v1/sources");

        var id = Assert.Single(payload!.Sources);
        Assert.Equal(MediaRackFactory.Single, id.Id);
    }

    /// <summary>
    /// 总数必须按可见源算。这条是"过滤有没有下到 SQL 层"的探针：
    /// 若在内存里过滤，total 会是全库的 3，翻页也会错。
    /// </summary>
    [Fact]
    public async Task Aggregate_view_counts_only_visible_sources()
    {
        var page = await Authorized.GetFromJsonAsync<MediaPageDto>("/api/v1/library");

        Assert.Equal(1, page!.Total);

        var item = Assert.Single(page.Items);
        Assert.Equal(MediaRackFactory.KeySingle, item.Key);
        Assert.Equal(MediaRackFactory.Single, item.SourceId);
    }

    /// <summary>存在但不可见，返回 404 而不是 403：403 等于承认"这个源存在"。</summary>
    [Fact]
    public async Task Invisible_source_is_404_not_403()
    {
        var response = await Authorized.GetAsync($"/api/v1/sources/{MediaRackFactory.DualA}/library");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("source_not_found", error!.Code);
    }

    [Fact]
    public async Task Invisible_media_detail_is_404()
    {
        var response = await Authorized.GetAsync(
            $"/api/v1/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Invisible_source_stream_is_404()
    {
        var response = await Authorized.GetAsync(
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_only_service_rejects_writes()
    {
        var response = await Authorized.PostAsJsonAsync(
            $"/api/v1/meta/{MediaRackFactory.Single}/{MediaRackFactory.KeySingle}",
            new { titleDisplay = "改个名" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("read_only_service", error!.Code);
    }

    [Fact]
    public async Task Read_only_service_rejects_scan()
    {
        var response = await Authorized.PostAsJsonAsync("/api/v1/scan",
            new { sourceId = MediaRackFactory.Single });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// cookie 名必须带端口。cookie 的匹配规则里没有端口，叫同一个名字就会
    /// "登录 8080 顺带解锁 8081"。
    /// </summary>
    [Fact]
    public async Task Login_sets_a_port_scoped_cookie()
    {
        var response = await Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { token = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        var cookie = Assert.Single(cookies);

        Assert.StartsWith("mrack_token_s", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("mrack_token=s3cret", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_password_is_401()
    {
        var response = await Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { token = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>健康检查免鉴权，但源列表要按服务过滤：不能借探活把别的源数出来。</summary>
    [Fact]
    public async Task Health_lists_only_visible_sources()
    {
        var health = await Anonymous.GetFromJsonAsync<HealthDto>("/api/v1/health");

        Assert.True(health!.AuthRequired);
        Assert.Null(health.MetaRoot);

        var source = Assert.Single(health.Sources);
        Assert.Equal(MediaRackFactory.Single, source.Id);
    }

    private sealed record SourceListPayload(List<SourceIdPayload> Sources);

    private sealed record SourceIdPayload(string Id);
}
