using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaRack.Store;
using Xunit;

namespace MediaRack.Server.Tests;

/// <summary>
/// 源门户（<c>/s/{slug}</c>）与管理页端点。
/// <para>
/// 这组测试守的是安全边界与热生效两个命题：
/// 令牌错 = 401、源不存在 = 404（不区分两者细节以防枚举）、门户下管理端点必须被拒、
/// 建源之后不需要重启就能从门户访问。种子库三个源的 slug/token 由
/// <see cref="SourceRepository.EnsureIdentity"/> 在迁移末尾补齐，这里直接开库读实际值。
/// </para>
/// </summary>
public sealed class SourcePortalTests : IClassFixture<MediaRackFactory>
{
    private readonly MediaRackFactory _factory;

    public SourcePortalTests(MediaRackFactory factory) => _factory = factory;

    /// <summary>开同一个库读实际生成的身份，避免测试里复刻生成规则。</summary>
    private (string Slug, string Token) IdentityOf(string sourceId)
    {
        using var db = LibraryDatabase.Open(_factory.MetaRoot);
        var source = new SourceRepository(db).Get(sourceId)!;
        return (source.Slug!, source.Token!);
    }

    // ── 鉴权边界 ─────────────────────────────────────────────

    [Fact]
    public async Task Portal_without_token_returns_401()
    {
        var (slug, _) = IdentityOf(MediaRackFactory.DualA);
        var response = await _factory.CreateClient().GetAsync($"/s/{slug}/api/v1/library");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Portal_with_wrong_token_returns_401()
    {
        var (slug, _) = IdentityOf(MediaRackFactory.DualA);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "totally-wrong");

        var response = await client.GetAsync($"/s/{slug}/api/v1/library");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Portal_with_bearer_token_serves_only_that_source()
    {
        var (slug, token) = IdentityOf(MediaRackFactory.DualA);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync($"/s/{slug}/api/v1/library");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, item =>
            Assert.Equal(MediaRackFactory.DualA, item.GetProperty("sourceId").GetString()));
        // 种子库：源 a 有 KeyOnlyA 与 KeyShared 两条。
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Portal_query_token_serves_library_for_url_only_players()
    {
        var (slug, token) = IdentityOf(MediaRackFactory.DualA);

        var response = await _factory.CreateClient()
            .GetAsync($"/s/{slug}/api/v1/library?token={token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Portal_unknown_slug_returns_404_not_401()
    {
        // 404 而不是 401：401 等于承认"slug 对，只是令牌错"，枚举者可以据此确认源存在。
        var response = await _factory.CreateClient().GetAsync("/s/nope/api/v1/library");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Portal_sources_lists_only_own_source()
    {
        var (slug, token) = IdentityOf(MediaRackFactory.DualB);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var payload = await client.GetFromJsonAsync<JsonDocument>($"/s/{slug}/api/v1/sources");

        var sources = payload!.RootElement.GetProperty("sources");
        Assert.Equal(1, sources.GetArrayLength());
        Assert.Equal(MediaRackFactory.DualB, sources[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Portal_without_trailing_slash_redirects()
    {
        var (slug, _) = IdentityOf(MediaRackFactory.DualA);
        // 关掉自动跟随，否则 301 会被 HttpClient 静默追到带斜杠的页面。
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync($"/s/{slug}");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"/s/{slug}/", response.Headers.Location?.ToString());
    }

    // ── 管理页边界 ───────────────────────────────────────────

    [Fact]
    public async Task Root_path_serves_admin_page_and_services_endpoint()
    {
        // WebApplicationFactory 没有远端地址，SourcePortal 按本机处理（见 IsLoopback 注释）。
        var page = await _factory.CreateClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);

        var services = await _factory.CreateClient().GetFromJsonAsync<JsonDocument>("/api/v1/services");

        // 只断言"返回的 id 都出自种子集合"，不校验总数：别的用例会改 services 配置，用例间顺序无保证。
        var ids = services!.RootElement.GetProperty("sources")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()!)
            .ToHashSet();
        Assert.Subset(ids, new HashSet<string> { MediaRackFactory.DualA, MediaRackFactory.DualB, MediaRackFactory.Single });
    }

    [Fact]
    public async Task Admin_endpoints_are_rejected_from_a_portal()
    {
        // 拿着 a 源的令牌绝不能读到 b 源的令牌，否则源级隔离白做。
        var (slug, token) = IdentityOf(MediaRackFactory.DualA);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync($"/s/{slug}/api/v1/services");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── 热生效：建源、改 slug、换令牌都不用重启 ─────────────

    [Fact]
    public async Task Created_source_is_served_immediately_without_restart()
    {
        // 与既有 single 源同一个 root：多对多，一个文件夹允许挂成多个源。
        var content = JsonContent.Create(new
        {
            root = _factory.SingleRoot,
            name = "副本",
            slug = "copy1",
            token = "hand-written-token-0123456789",
        });

        var created = await _factory.CreateClient().PostAsync("/api/v1/sources", content);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var (slug, token) = ("copy1", "hand-written-token-0123456789");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync($"/s/{slug}/api/v1/sources");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sources = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sources");
        Assert.Equal(1, sources.GetArrayLength());
        Assert.Equal("copy1", sources[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Same_root_can_be_registered_as_two_sources()
    {
        foreach (var slug in new[] { "twin1", "twin2" })
        {
            var created = await _factory.CreateClient().PostAsync("/api/v1/sources", JsonContent.Create(new
            {
                root = _factory.SingleRoot,
                slug,
                token = "tok-" + slug,
            }));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }
    }

    [Fact]
    public async Task Slug_rename_kills_old_portal_and_serves_new_one()
    {
        var (slug, token) = IdentityOf(MediaRackFactory.DualA);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // 建一个专属源再改，避免影响别的用例共享的种子源。
        await client.PostAsync("/api/v1/sources", JsonContent.Create(new
        {
            root = _factory.SingleRoot,
            slug = "renameme",
            token,
        }));

        var renamed = await client.PatchAsJsonAsync("/api/v1/sources/renameme", new { slug = "renamed" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var oldPortal = await client.GetAsync("/s/renameme/api/v1/sources");
        Assert.Equal(HttpStatusCode.NotFound, oldPortal.StatusCode);

        var newPortal = await client.GetAsync("/s/renamed/api/v1/sources");
        Assert.Equal(HttpStatusCode.OK, newPortal.StatusCode);
    }

    [Fact]
    public async Task Token_reset_takes_effect_immediately()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/api/v1/sources", JsonContent.Create(new
        {
            root = _factory.SingleRoot,
            slug = "retok",
            token = "old-token-0123456789",
        }));

        var patched = await client.PatchAsJsonAsync("/api/v1/sources/retok", new { token = "new-token-9876543210" });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var withOld = await _factory.CreateClient().GetAsync("/s/retok/api/v1/library?token=old-token-0123456789");
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);

        var withNew = await _factory.CreateClient().GetAsync("/s/retok/api/v1/library?token=new-token-9876543210");
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    // ── 配对二维码 ───────────────────────────────────────────
    //
    // 两种形态：给了 host = 单地址纯 URL 码（老客户端兼容）；省略 host = 配对码，
    // 内容为 MR1|{json}，打包全部候选地址由手机端探活择优。
    // SVG 里是编码后的图形，没法断言"装了哪些地址"——这里守的是"两种形态都能出码"
    // 和"host 仍必须是本机地址"这两条边界；内容解析由 App 侧单测覆盖。

    [Fact]
    public async Task Pairing_qr_single_host_and_packed_variants()
    {
        var client = _factory.CreateClient();
        var host = MediaRack.Server.Hosting.HostCatalog.Addresses().FirstOrDefault();
        if (host is null) return; // 无网卡的极端环境跳过（CI 容器）

        var single = await client.GetAsync($"/api/v1/auth/pairing/qr?source={MediaRackFactory.DualA}&host={host}");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Contains("image/svg+xml", single.Content.Headers.ContentType?.ToString() ?? "");

        // 省略 host → 配对码（打包候选地址）
        var packed = await client.GetAsync($"/api/v1/auth/pairing/qr?source={MediaRackFactory.DualA}");
        Assert.Equal(HttpStatusCode.OK, packed.StatusCode);
        Assert.Contains("image/svg+xml", packed.Content.Headers.ContentType?.ToString() ?? "");

        // host 不在本机地址列表里 → 400（这个端点不能被当成通用二维码生成器）
        var bad = await client.GetAsync("/api/v1/auth/pairing/qr?source=x&host=8.8.8.8");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ── 输入校验 ─────────────────────────────────────────────
    [Fact]
    public async Task Create_source_rejects_bad_slug_and_duplicates()
    {
        var bad = await _factory.CreateClient().PostAsync("/api/v1/sources", JsonContent.Create(new
        {
            slug = "中文源",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // 先建再撞，不依赖用例执行顺序。手写 slug 撞名 = Conflict（用户明确要这个名字，
        // 悄悄改成 dup1-2 是惊吓）；自动生成撞名才走 -2 后缀。
        await _factory.CreateClient().PostAsync("/api/v1/sources", JsonContent.Create(new
        {
            slug = "dup1",
            token = "tok-dup-0123456789",
        }));
        var duplicate = await _factory.CreateClient().PostAsync("/api/v1/sources", JsonContent.Create(new
        {
            slug = "dup1",
            token = "tok-dup",
        }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // 不填 slug：自动生成并自动避开占用（source / source-2 …）。
        var auto = await _factory.CreateClient().PostAsync("/api/v1/sources", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, auto.StatusCode);
    }

    [Fact]
    public async Task Create_source_starts_empty_then_add_folders()
    {
        var client = _factory.CreateClient();

        // 建源：无文件夹，条目数为 0
        var created = await client.PostAsync("/api/v1/sources", JsonContent.Create(new
        {
            slug = "multidir",
            token = "tok-multidir-01234",
        }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        // 添加文件夹：可以同时挂 single + dual 两个（布局跟文件夹走）
        var singleDir = _factory.SingleRoot;
        var added = await client.PostAsync("/api/v1/sources/multidir/folders",
            JsonContent.Create(new { root = singleDir, layout = "single" }));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        using (var doc = JsonDocument.Parse(await added.Content.ReadAsStringAsync()))
        {
            var folders = doc.RootElement.GetProperty("source").GetProperty("folders");
            Assert.Equal(1, folders.GetArrayLength());
            Assert.Equal("single", folders[0].GetProperty("layout").GetString());
        }

        // 重复登记同一路径 → 409
        var dupFolder = await client.PostAsync("/api/v1/sources/multidir/folders",
            JsonContent.Create(new { root = singleDir }));
        Assert.Equal(HttpStatusCode.Conflict, dupFolder.StatusCode);

        // 不存在的目录 → 400
        var badDir = await client.PostAsync("/api/v1/sources/multidir/folders",
            JsonContent.Create(new { root = Path.Combine(_factory.Root, "no-such-dir") }));
        Assert.Equal(HttpStatusCode.BadRequest, badDir.StatusCode);

        // 热生效：加完文件夹门户立刻能访问（不用重启）
        var lib = await client.GetAsync("/s/multidir/api/v1/library?token=tok-multidir-01234");
        Assert.Equal(HttpStatusCode.OK, lib.StatusCode);
    }

    [Fact]
    public async Task Create_source_with_blank_token_generates_one()
    {
        var response = await _factory.CreateClient().PostAsync("/api/v1/sources", JsonContent.Create(new
        {
            root = _factory.SingleRoot,
            slug = "emptytok",
            token = "   ",
        }));

        // 空令牌会被生成一个替换掉（AdminEndpoints）：不能真的存成空白，否则这个源就公开了。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("source").GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.NotEqual("   ", token);
    }
}
