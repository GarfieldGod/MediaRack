using MediaRack.Client.Remote;
using MediaRack.Client.Storage;

namespace MediaRack.Client.Tools;

/// <summary>
/// 协议层自检（无界面）：<c>MediaRack.Client --selftest [baseUrl]</c>
///
/// 客户端每段里程碑都要有可执行的验收。UI 之外的东西（地址规范化 / 配对码 / 探活 /
/// 鉴权 / 契约字段）不该靠"点一遍看看"，这一条命令把它们全跑一遍，输出可贴进验收记录。
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string baseUrl)
    {
        var ok = true;
        void Say(string tag, bool pass, string detail)
        {
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {tag}：{detail}");
            if (!pass) ok = false;
        }

        Console.WriteLine($"MediaRack.Client 协议层自检 → {baseUrl}");
        Console.WriteLine();

        // 1. 本地纯逻辑：地址规范化 + 配对码解析（不需要服务端）
        Console.WriteLine("1. 本地逻辑");

        const string pairing = """MR1|{"token":"tk-abc","urls":["http://127.0.0.1:8099/s/demo","http://192.0.2.5:8080"]}""";
        var parsed = PairingCode.Parse(pairing);
        Say("配对码 MR1|", parsed is { Token: "tk-abc" } p && p.Urls.Count == 2,
            parsed is null ? "解析为 null" : $"token={parsed.Token}，{parsed.Urls.Count} 个候选");

        var hash = PairingCode.Parse("http://192.0.2.10:8080/s/movie#token=xyz");
        Say("单地址码 #token=", hash is { Token: "xyz" } && hash.Urls.Count == 1 && hash.Urls[0].EndsWith("/s/movie"),
            hash is null ? "解析为 null" : $"{hash.Urls[0]} token={hash.Token}");

        Say("地址规范化", ApiClient.NormalizeBaseUrl("192.0.2.10:8080") == "http://192.0.2.10:8080"
                         && ApiClient.NormalizeBaseUrl("http://h:8080/") == "http://h:8080"
                         && ApiClient.NormalizeBaseUrl("http://h:8080/x?y=1") is null,
            "补 scheme / 去尾斜杠 / 拒绝 query");

        Say("slug 规则", ApiClient.IsValidSlug("movie") && ApiClient.IsValidSlug("my-lib_2")
                         && !ApiClient.IsValidSlug("Movie") && !ApiClient.IsValidSlug(""),
            "小写开头、可含 -_、禁止大写");

        Console.WriteLine();

        // 2. 服务端连通
        Console.WriteLine("2. 服务端");
        using var api = new ApiClient(baseUrl);

        HealthDto health;
        try
        {
            health = await api.HealthAsync();
            Say("health", true, $"status={health.Status} version={health.Version} schema={health.SchemaVersion}");
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Say("health", false, e.Message);
            Console.WriteLine("\n连不上服务端，后续检查跳过。");
            return 1;
        }

        try
        {
            var auth = await api.AuthStateAsync();
            Say("auth/state", true, $"authRequired={auth.AuthRequired} authenticated={auth.Authenticated}");
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Say("auth/state", false, e.Message);
        }

        List<SourceInfoDto> sources = new();
        try
        {
            sources = await api.SourcesAsync();
            Say("sources", sources.Count > 0, string.Join("、", sources.Select(s => $"{s.Id}({s.ItemCount})")));
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Say("sources", false, e.Message);
        }

        MediaPageDto? page = null;
        try
        {
            page = await api.LibraryAsync(0, 3);
            Say("library", page.Items.Count > 0,
                $"total={page.Total} 前 {page.Items.Count} 条：" +
                string.Join(" / ", page.Items.Select(i => i.Title.Length > 16 ? i.Title[..16] + "…" : i.Title)));
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Say("library", false, e.Message);
        }

        if (page?.Items.Count > 0)
        {
            var first = page.Items[0];
            try
            {
                var detail = await api.MediaDetailAsync(first.SourceId, first.Key);
                var dual = api.DualStreamUrls(detail);
                var single = api.SingleStreamUrl(detail);
                var playable = dual is not null || single is not null;
                Say("详情与播放地址", playable,
                    $"layout={detail.Layout} delivery=[{string.Join(",", detail.Delivery)}] " +
                    (dual is not null ? "双流✓" : single is not null ? "单流✓" : "无可用地址"));

                if (dual is not null)
                {
                    Say("双流地址带 token", dual.Value.Video.Contains('/') && dual.Value.Audio.Length > 0,
                        $"video=…{dual.Value.Video[^28..]}");
                }
            }
            catch (Exception e) when (e is ApiException or NetworkException)
            {
                Say("详情与播放地址", false, e.Message);
            }
        }

        // 3. 浏览用的三个视图（第 2 段）：单源 / 合集 / 收藏夹
        Console.WriteLine();
        Console.WriteLine("3. 浏览视图");

        if (sources.Count > 0)
        {
            var src = sources.OrderByDescending(s => s.ItemCount).First();
            try
            {
                var own = await api.SourceLibraryAsync(src.Id, 0, 3);
                Say("单源列表", own.Total > 0 && own.Items.All(i => i.SourceId == src.Id),
                    $"{src.Id}: total={own.Total}，前 {own.Items.Count} 条来源一致");
            }
            catch (Exception e) when (e is ApiException or NetworkException)
            {
                Say("单源列表", false, e.Message);
            }
        }

        try
        {
            var collections = await api.CollectionsAsync();
            var top = collections.OrderByDescending(c => c.ItemCount).FirstOrDefault();
            Say("合集列表", collections.Count > 0,
                $"{collections.Count} 个合集，最大「{top?.Name}」{top?.ItemCount} 条");

            if (top is not null)
            {
                var inCol = await api.LibraryAsync(0, 2, collectionId: top.Id);
                Say("按合集过滤", inCol.Total > 0 && inCol.Total <= top.ItemCount,
                    $"collection={top.Id} → total={inCol.Total}");
            }
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Say("合集列表", false, e.Message);
        }

        try
        {
            var favs = await api.FavoritesAsync();
            var fav = favs.FirstOrDefault(f => f.ItemCount > 0);
            Say("收藏夹列表", true, $"{favs.Count} 个收藏夹" +
                (fav is null ? "（都没有条目）" : $"，取「{fav.Name}」{fav.ItemCount} 条"));

            if (fav is not null)
            {
                var inFav = await api.LibraryAsync(0, 2, favoriteId: fav.Id, sort: "added");
                Say("按收藏夹过滤", inFav.Total == fav.ItemCount,
                    $"favorite={fav.Id} sort=added → total={inFav.Total}（应等于 {fav.ItemCount}）");
            }
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Say("收藏夹列表", false, e.Message);
        }

        try
        {
            var q = await api.LibraryAsync(0, 3, query: "1");
            Say("搜索", true, $"q=1 → total={q.Total}");
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Say("搜索", false, e.Message);
        }

        // 4. 海报可下载（客户端要自己取图解码，不能只看 URL 拼得对）
        Console.WriteLine();
        Console.WriteLine("4. 海报");

        if (page?.Items.Count > 0)
        {
            var withPoster = page.Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.PosterUrl));
            if (withPoster is null)
            {
                Say("海报", false, "列表里没有带 posterUrl 的条目");
            }
            else
            {
                var url = api.MediaUrl(withPoster.PosterUrl!);
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var bytes = await http.GetByteArrayAsync(url);
                    // JPEG 头 FF D8 FF：拿到图就更深一层验证（404 会抛，不是图这里会露）
                    var isJpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
                    Say("海报下载", isJpeg, $"{bytes.Length} 字节，JPEG={isJpeg}，{url[^32..]}");
                }
                catch (Exception e)
                {
                    Say("海报下载", false, e.Message);
                }
            }
        }

        // 5. 连接配置存储（客户端自己的本地状态，不涉及服务端）
        Console.WriteLine();
        Console.WriteLine("5. 连接配置存储");

        var loaded = ConnectionStore.Load();
        Say("读取", true, $"{ConnectionStore.FilePath} → {loaded.Count} 条");

        var probe = "http://127.0.0.1:65500/probe-" + Guid.NewGuid().ToString("N")[..6];
        ConnectionStore.Upsert(probe, null, "自检探针");
        var hit = ConnectionStore.Load().FirstOrDefault(s => s.BaseUrl == probe);
        Say("写入往返", hit is not null && hit.LastUsedAt > 0,
            hit is null ? "写进去又读不到" : $"lastUsedAt={hit.LastUsedAt}（>0 表示落盘成功）");
        if (hit is not null) ConnectionStore.Remove(hit.Id);

        Console.WriteLine();
        Console.WriteLine(ok ? "全部通过。" : "有失败项，见上。");
        return ok ? 0 : 1;
    }
}
