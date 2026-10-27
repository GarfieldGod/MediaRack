using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MediaRack.Server.Tests;

/// <summary>
/// API 层测试。断言直接读 JSON 字段名，而不是反序列化成某个 C# 类型——
/// 这样字段改名会立刻失败，等于顺带校验了「实现与 contracts/openapi.v1.yaml 一致」。
/// </summary>
public sealed class ApiTests(MediaRackFactory factory) : IClassFixture<MediaRackFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<JsonDocument> GetJson(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> Send(
        string path, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (body is not null) request.Content = JsonContent.Create(body);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        var response = await _client.SendAsync(request);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> Patch(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(body) };
        var response = await _client.SendAsync(request);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> Delete(string path)
    {
        var response = await _client.DeleteAsync(path);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Health_ReportsSchemaVersionAndCapabilities()
    {
        using var doc = await GetJson("/api/v1/health");
        var root = doc.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        // 跟随程序支持的版本，而不是写死数字：schema 每升一版都改测试，等于把测试当待办清单。
        Assert.Equal(MediaRack.Store.LibraryDatabase.SupportedVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));

        // 三个源都在，且可达（临时目录确实存在）。
        var sources = root.GetProperty("sources");
        Assert.Equal(3, sources.GetArrayLength());
        Assert.All(sources.EnumerateArray(),
            s => Assert.True(s.GetProperty("reachable").GetBoolean()));

        Assert.True(root.GetProperty("capabilities").TryGetProperty("ffmpeg", out _));
    }

    [Fact]
    public async Task Sources_ReturnLayoutAndWriteLevel()
    {
        using var doc = await GetJson("/api/v1/sources");
        var sources = doc.RootElement.GetProperty("sources");

        var byId = sources.EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);

        Assert.Equal("dual", byId[MediaRackFactory.DualA].GetProperty("layout").GetString());
        Assert.Equal("single", byId[MediaRackFactory.Single].GetProperty("layout").GetString());

        // 只读档必须如实报出来——客户端据此决定要不要显示编辑入口。
        Assert.Equal("none", byId[MediaRackFactory.Single].GetProperty("virtualWrite").GetString());
        Assert.Equal("full", byId[MediaRackFactory.DualA].GetProperty("virtualWrite").GetString());
    }

    [Fact]
    public async Task Library_AggregateViewDeduplicatesByKey()
    {
        using var doc = await GetJson("/api/v1/library");
        var root = doc.RootElement;

        // A 有 2 条、B 有 1 条、single 有 1 条，其中 KeyShared 在两个源里重复 → 去重后 3 条。
        Assert.Equal(3, root.GetProperty("total").GetInt32());

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        var shared = items.Single(i => i.GetProperty("key").GetString() == MediaRackFactory.KeyShared);

        // 去重只在聚合视图发生：这里必须列出两个源，并指明优先用哪个。
        var sources = shared.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal([MediaRackFactory.DualA, MediaRackFactory.DualB], sources);
        Assert.Equal(MediaRackFactory.DualA, shared.GetProperty("preferred").GetString());
        Assert.Equal(MediaRackFactory.DualA, shared.GetProperty("sourceId").GetString());
    }

    [Fact]
    public async Task Library_SingleSourceViewKeepsBothCopies()
    {
        using var doc = await GetJson($"/api/v1/sources/{MediaRackFactory.DualA}/library");

        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());

        // sources / preferred 只属于聚合视图，单源视图里必须是 null。
        var first = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, first.GetProperty("sources").ValueKind);
    }

    [Fact]
    public async Task Library_PagesWithOffsetAndLimit()
    {
        using var page1 = await GetJson("/api/v1/library?limit=2&offset=0");
        Assert.Equal(2, page1.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page1.RootElement.GetProperty("nextOffset").GetInt32());

        using var page2 = await GetJson("/api/v1/library?limit=2&offset=2");
        Assert.Equal(1, page2.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page2.RootElement.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task Library_SearchesTitle()
    {
        using var doc = await GetJson("/api/v1/library?q=" + Uri.EscapeDataString("第一集"));
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());

        using var none = await GetJson("/api/v1/library?q=" + Uri.EscapeDataString("不存在的标题"));
        Assert.Equal(0, none.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Library_UnknownSource_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/sources/nope/library");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("source_not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Media_DetailExposesTracksAndDelivery()
    {
        using var doc = await GetJson($"/api/v1/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}");
        var root = doc.RootElement;

        Assert.Equal("dual", root.GetProperty("layout").GetString());
        Assert.Equal("只在 A 的视频", root.GetProperty("title").GetString());

        var tracks = root.GetProperty("tracks").EnumerateArray().ToList();
        Assert.Equal(2, tracks.Count);
        Assert.Equal("video", tracks[0].GetProperty("role").GetString());
        Assert.Equal("audio", tracks[1].GetProperty("role").GetString());

        // 两边都能用时，direct 与 remux 都该报出来。
        var delivery = root.GetProperty("delivery").EnumerateArray().Select(d => d.GetString()).ToList();
        Assert.Contains("direct", delivery);
    }

    [Fact]
    public async Task Media_UnknownItem_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/media/{MediaRackFactory.DualA}/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Meta_OnReadOnlySource_Returns403_AndLeavesNoRow()
    {
        var (status, body) = await Send(
            $"/api/v1/meta/{MediaRackFactory.Single}/{MediaRackFactory.KeySingle}",
            new { titleDisplay = "改不动" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("read_only_source", body.RootElement.GetProperty("code").GetString());

        // 闸门在写入之前：被拒绝时库里连一条 rev=0 的记录都不该留下。
        using var detail = await GetJson(
            $"/api/v1/media/{MediaRackFactory.Single}/{MediaRackFactory.KeySingle}");
        Assert.Equal(0, detail.RootElement.GetProperty("meta").GetProperty("rev").GetInt64());
        Assert.Equal(JsonValueKind.Null,
            detail.RootElement.GetProperty("meta").GetProperty("titleDisplay").ValueKind);
    }

    [Fact]
    public async Task Meta_IncrementsRev_AndClearsTitleWithEmptyString()
    {
        const string title = "改名后";

        var (status, body) = await Send(
            $"/api/v1/meta/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}",
            new { titleDisplay = title });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(title, body.RootElement.GetProperty("titleDisplay").GetString());
        var rev = body.RootElement.GetProperty("rev").GetInt64();
        Assert.True(rev >= 1);

        // 空串 = 清除覆盖、恢复源端标题。不需要单独的 unset 动词。
        var (clearStatus, cleared) = await Send(
            $"/api/v1/meta/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}",
            new { titleDisplay = "" });

        Assert.Equal(HttpStatusCode.OK, clearStatus);
        Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("titleDisplay").ValueKind);
        Assert.True(cleared.RootElement.GetProperty("rev").GetInt64() > rev);

        // 列表里也该回到源端标题。
        using var doc = await GetJson($"/api/v1/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}");
        Assert.Equal("只在 A 的视频", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Meta_WithStaleIfMatch_Returns409()
    {
        var (_, created) = await Send(
            $"/api/v1/meta/{MediaRackFactory.DualA}/{MediaRackFactory.KeyShared}",
            new { titleDisplay = "第一次" });

        var rev = created.RootElement.GetProperty("rev").GetInt64();

        var (status, body) = await Send(
            $"/api/v1/meta/{MediaRackFactory.DualA}/{MediaRackFactory.KeyShared}",
            new { titleDisplay = "第二次" },
            ifMatch: (rev - 1).ToString());

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("rev_conflict", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Meta_WithMatchingIfMatch_Succeeds()
    {
        var (_, created) = await Send(
            $"/api/v1/meta/{MediaRackFactory.DualB}/{MediaRackFactory.KeyShared}",
            new { titleDisplay = "带锁写入" });

        var rev = created.RootElement.GetProperty("rev").GetInt64();

        // HTTP 规范允许带引号，两种写法都得认。
        var (status, body) = await Send(
            $"/api/v1/meta/{MediaRackFactory.DualB}/{MediaRackFactory.KeyShared}",
            new { titleDisplay = "带锁写入 v2" },
            ifMatch: $"\"{rev}\"");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(rev + 1, body.RootElement.GetProperty("rev").GetInt64());
    }

    [Fact]
    public async Task HiddenItems_AreExcludedUnlessRequested()
    {
        // 聚合视图按 key 去重：只隐藏其中一个源不会让这条内容消失，
        // 所以两个源都要隐藏——这条断言的正是"去重后的可见性"。
        await Send($"/api/v1/meta/{MediaRackFactory.DualA}/{MediaRackFactory.KeyShared}",
            new { hidden = true });
        await Send($"/api/v1/meta/{MediaRackFactory.DualB}/{MediaRackFactory.KeyShared}",
            new { hidden = true });

        using var visible = await GetJson("/api/v1/library");
        var visibleKeys = visible.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.DoesNotContain(MediaRackFactory.KeyShared, visibleKeys);

        using var all = await GetJson("/api/v1/library?includeHidden=true");
        var allKeys = all.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Contains(MediaRackFactory.KeyShared, allKeys);

        // 单源视图的隐藏过滤必须和聚合视图一致，否则 Web 页那个勾选框在一个视图下像坏了。
        using var sourceDefault = await GetJson($"/api/v1/sources/{MediaRackFactory.DualA}/library");
        Assert.Equal(1, sourceDefault.RootElement.GetProperty("total").GetInt32());

        using var sourceAll = await GetJson(
            $"/api/v1/sources/{MediaRackFactory.DualA}/library?includeHidden=true");
        Assert.Equal(2, sourceAll.RootElement.GetProperty("total").GetInt32());

        // 复原，避免影响同一 fixture 里的其他测试。
        await Send($"/api/v1/meta/{MediaRackFactory.DualA}/{MediaRackFactory.KeyShared}",
            new { hidden = false });
        await Send($"/api/v1/meta/{MediaRackFactory.DualB}/{MediaRackFactory.KeyShared}",
            new { hidden = false });
    }

    [Fact]
    public async Task Progress_ResetsPositionWhenCompleted()
    {
        var (status, body) = await Send(
            $"/api/v1/progress/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}",
            new { posMs = 4321 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(4321, body.RootElement.GetProperty("playPosMs").GetInt64());

        var (doneStatus, done) = await Send(
            $"/api/v1/progress/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}",
            new { posMs = 11_900, completed = true });

        Assert.Equal(HttpStatusCode.OK, doneStatus);
        // 看完了就归零，否则下次"继续观看"会从片尾开始。
        Assert.Equal(0, done.RootElement.GetProperty("playPosMs").GetInt64());
        Assert.True(done.RootElement.GetProperty("playCount").GetInt32() >= 2);
    }

    [Fact]
    public async Task Collections_AreFlatPerSource_WithFolderAndUpKinds()
    {
        using var doc = await GetJson("/api/v1/collections");
        var rows = doc.RootElement.EnumerateArray().ToList();
        var ids = rows.Select(c => c.GetProperty("id").GetString()!).ToList();

        // single 源：只有"有视频的目录"成合集。种子数据里 FirstEpisode 在 片子/ 下，
        // 源根没有散落视频 → 不存在根合集（v4：没视频不成合集）。
        Assert.Contains($"dir:{MediaRackFactory.Single}:/片子", ids);
        Assert.DoesNotContain($"dir:{MediaRackFactory.Single}:/", ids);

        // dual 源：按 UP 主分组（种子数据没有 owner → unknown 合集）
        Assert.Contains($"up:{MediaRackFactory.DualA}:unknown", ids);

        var folder = rows.Single(c => c.GetProperty("id").GetString() == $"dir:{MediaRackFactory.Single}:/片子");
        Assert.Equal("folder", folder.GetProperty("kind").GetString());
        Assert.Equal("片子", folder.GetProperty("name").GetString());
        Assert.Equal(1, folder.GetProperty("itemCount").GetInt32());

        // 按源过滤
        using var single = await GetJson($"/api/v1/collections?source={MediaRackFactory.Single}");
        Assert.All(single.RootElement.EnumerateArray(),
            c => Assert.Equal(MediaRackFactory.Single, c.GetProperty("sourceId").GetString()));
    }

    [Fact]
    public async Task Collections_FilterLibraryByExclusiveCollection()
    {
        using var doc = await GetJson("/api/v1/collections?source=" + MediaRackFactory.Single);
        var cid = doc.RootElement.EnumerateArray()
            .First(c => c.GetProperty("kind").GetString() == "folder")
            .GetProperty("id").GetString()!;

        using var filtered = await GetJson($"/api/v1/library?collection={Uri.EscapeDataString(cid)}");
        var total = filtered.RootElement.GetProperty("total").GetInt32();
        Assert.Equal(1, total);
        Assert.Equal(MediaRackFactory.KeySingle,
            filtered.RootElement.GetProperty("items")[0].GetProperty("key").GetString());

        // 条目摘要里带着所属合集（客户端据此显示"来自哪个 UP 主/文件夹"）
        var item = filtered.RootElement.GetProperty("items")[0];
        Assert.Equal(cid, item.GetProperty("collectionId").GetString());
        Assert.Equal("片子", item.GetProperty("collectionName").GetString());
    }

    [Fact]
    public async Task Favorites_CrudAndItemMembership()
    {
        var name = "测试收藏夹-" + Guid.NewGuid().ToString("N")[..6];
        var (status, created) = await Send("/api/v1/favorites", new { name });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = created.RootElement.GetProperty("id").GetString()!;
        Assert.StartsWith("fav:", id);

        // 收藏一个条目
        var (addStatus, addBody) = await Send($"/api/v1/favorites/{id}/items", new
        {
            add = new[] { new { source = MediaRackFactory.DualA, key = MediaRackFactory.KeyOnlyA } },
        });
        Assert.Equal(HttpStatusCode.OK, addStatus);
        Assert.Equal(1, addBody.RootElement.GetProperty("added").GetInt32());

        // 收藏夹视图：/library?favorite=<id>
        using (var filtered = await GetJson($"/api/v1/library?favorite={Uri.EscapeDataString(id)}"))
        {
            Assert.Equal(1, filtered.RootElement.GetProperty("total").GetInt32());
        }

        // 收藏一个合集（关注 UP 主）
        using var cols = await GetJson("/api/v1/collections");
        var cid = cols.RootElement.EnumerateArray().First().GetProperty("id").GetString()!;
        var (colStatus, colBody) = await Send($"/api/v1/favorites/{id}/collections", new { add = new[] { cid } });
        Assert.Equal(HttpStatusCode.OK, colStatus);
        Assert.Equal(1, colBody.RootElement.GetProperty("added").GetInt32());

        using (var got = await GetJson($"/api/v1/favorites/{id}/collections"))
        {
            Assert.Equal(cid, got.RootElement.EnumerateArray().Single().GetProperty("id").GetString());
        }

        // 改名 + 删除
        var (renameStatus, _) = await Patch($"/api/v1/favorites/{id}", new { name = name + "-改" });
        Assert.Equal(HttpStatusCode.OK, renameStatus);

        var (delStatus, _) = await Delete($"/api/v1/favorites/{id}");
        Assert.Equal(HttpStatusCode.OK, delStatus);
    }

    [Fact]
    public async Task Favorites_SortByAddedPutsNewestFirst()
    {
        // 两个夹子同一个条目对、加入顺序相反：
        // 若 sort 没生效（退化成标题序），两个夹子的结果必然相同，以下两条断言就必有一条不成立。
        // 用这种"对照"而不是断言某个固定顺序，是为了不依赖这两个条目的标题与隐藏状态
        // ——同一 fixture 里的其他用例会改它们。
        var fav1 = await NewFavorite("排序A");
        var fav2 = await NewFavorite("排序B");

        await AddItem(fav1, MediaRackFactory.KeyShared);
        await Task.Delay(20); // added_at 是毫秒时间戳，同毫秒会退化成按标题兜底
        await AddItem(fav1, MediaRackFactory.KeyOnlyA);

        await AddItem(fav2, MediaRackFactory.KeyOnlyA);
        await Task.Delay(20);
        await AddItem(fav2, MediaRackFactory.KeyShared);

        Assert.Equal(
            [MediaRackFactory.KeyOnlyA, MediaRackFactory.KeyShared],
            await KeysOf($"/api/v1/library?favorite={Uri.EscapeDataString(fav1)}&sort=added"));

        Assert.Equal(
            [MediaRackFactory.KeyShared, MediaRackFactory.KeyOnlyA],
            await KeysOf($"/api/v1/library?favorite={Uri.EscapeDataString(fav2)}&sort=added"));

        await Delete($"/api/v1/favorites/{fav1}");
        await Delete($"/api/v1/favorites/{fav2}");
    }

    private async Task<string> NewFavorite(string prefix)
    {
        var (_, created) = await Send("/api/v1/favorites", new { name = prefix + "-" + Guid.NewGuid().ToString("N")[..6] });
        return created.RootElement.GetProperty("id").GetString()!;
    }

    private async Task AddItem(string favoriteId, string key) =>
        await Send($"/api/v1/favorites/{favoriteId}/items", new
        {
            add = new[] { new { source = MediaRackFactory.DualA, key } },
        });

    private async Task<List<string?>> KeysOf(string path)
    {
        using var doc = await GetJson(path);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("key").GetString()).ToList();
    }

    [Fact]
    public async Task Scan_AcceptsJobAndCompletes()
    {
        // 扫自己的空源，不动共享种子数据：扫描会 MarkMissingExcept——
        // 若直接扫 DualA 这类种子源，种子目录没有 entry.json、扫描认不出条目，
        // 会把种子里 status='ok' 的行整体打成 missing，污染同一 fixture 里后面的用例。
        var scanDir = Path.Combine(factory.Root, "scan-target");
        Directory.CreateDirectory(scanDir);
        var src = await factory.CreateClient().PostAsync("/api/v1/sources",
            JsonContent.Create(new { slug = "scan-target", token = "tok-scan-target-0123" }));
        Assert.Equal(HttpStatusCode.OK, src.StatusCode);
        var add = await factory.CreateClient().PostAsync("/api/v1/sources/scan-target/folders",
            JsonContent.Create(new { root = scanDir }));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        var payload = new { sourceId = "scan-target", verify = "none" };

        var response = await _client.PostAsJsonAsync("/api/v1/scan", payload);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobId = accepted.RootElement.GetProperty("jobId").GetString()!;

        // 扫描是异步的：轮询到 done/failed 为止，超时即失败。
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string state;

        while (true)
        {
            using var job = await GetJson($"/api/v1/scan/{jobId}");
            state = job.RootElement.GetProperty("state").GetString()!;
            if (state is "done" or "failed") break;

            Assert.True(DateTime.UtcNow < deadline, $"扫描任务超时，最后状态：{state}");
            await Task.Delay(200);
        }

        Assert.Equal("done", state);
    }

    [Fact]
    public async Task Scan_WithoutSourceOrRoot_Returns400()
    {
        var (status, body) = await Send("/api/v1/scan", new { verify = "sample" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", body.RootElement.GetProperty("code").GetString());
    }
}
