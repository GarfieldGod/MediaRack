using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MediaRack.Server.Json;
using Xunit;

namespace MediaRack.Server.Tests;

/// <summary>
/// 配对端点（主路径见 docs/design.md §10）：它会把令牌本身交出去，所以鉴权与 host 白名单是硬约束。
/// WebApplicationFactory 没有真实端口，连接回落到第一个服务（见 <see cref="MultiServiceTests"/> 说明）。
/// </summary>
public sealed class PairingTests : IClassFixture<PairingTests.LockedFactory>
{
    private const string Password = "s3cret";

    /// <summary>带密码的服务。port 随意：WebApplicationFactory 下 LocalPort 恒为 0。</summary>
    public sealed class LockedFactory : MediaRackFactory
    {
        public LockedFactory() : base(null, """
            [{"name":"全部","port":8080,"password":"s3cret"}]
            """)
        { }
    }

    private readonly LockedFactory _factory;

    public PairingTests(LockedFactory factory) => _factory = factory;

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
    public async Task Anonymous_gets_401_because_payload_contains_the_token()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/auth/pairing");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("unauthorized", error!.Code);
    }

    [Fact]
    public async Task Authorized_gets_hosts_and_token()
    {
        var payload = await Authorized.GetFromJsonAsync<PairingDto>("/api/v1/auth/pairing");

        Assert.NotEmpty(payload!.Hosts);
        // 不该有环回与 APIPA
        Assert.All(payload.Hosts, h =>
        {
            Assert.False(h.StartsWith("127.", StringComparison.Ordinal), h);
            Assert.False(h.StartsWith("169.254.", StringComparison.Ordinal), h);
        });
        Assert.Equal(Password, payload.Token);
    }

    [Fact]
    public async Task Qr_returns_svg_for_a_local_host()
    {
        var info = await Authorized.GetFromJsonAsync<PairingDto>("/api/v1/auth/pairing");
        var host = info!.Hosts[0];

        var response = await Authorized.GetAsync($"/api/v1/auth/pairing/qr?host={host}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.Contains("<svg", svg);
    }

    [Fact]
    public async Task Qr_rejects_foreign_hosts()
    {
        var response = await Authorized.GetAsync("/api/v1/auth/pairing/qr?host=8.8.8.8");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("bad_host", error!.Code);
    }

    [Fact]
    public async Task Public_service_allows_anonymous_and_token_is_null()
    {
        using var factory = new OpenFactory();
        var response = await factory.CreateClient().GetAsync("/api/v1/auth/pairing");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<PairingDto>();
        Assert.NotEmpty(payload!.Hosts);
        Assert.Null(payload.Token);
    }

    public sealed class OpenFactory : MediaRackFactory
    {
        public OpenFactory() : base(null, """[{"name":"全部","port":8080}]""") { }
    }
}
