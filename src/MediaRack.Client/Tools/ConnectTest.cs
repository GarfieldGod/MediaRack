using MediaRack.Client.Remote;
using MediaRack.Client.Storage;
using MediaRack.Client.ViewModels;

namespace MediaRack.Client.Tools;

/// <summary>
/// 连接页的自动化验收：<c>MediaRack.Client --connecttest &lt;源门户地址&gt; [令牌]</c>
///
/// 守的是那条不能让的线——<b>一条配置只连一个源</b>。核心是两件事：
/// <list type="number">
/// <item>表单必须三段齐全，源地址留空不能让"连接"变可用；</item>
/// <item>连上的结果只能有一条源，多一条都算失败。</item>
/// </list>
/// 顺带把"服务端根路径确实能列出全部源"也测出来：这正是客户端必须自己堵死它的原因，
/// 光靠服务端拦不住——桌面客户端和服务端常常在同一台机器上，loopback 是放行的。
/// </summary>
public static class ConnectTest
{
    public static async Task<int> RunAsync(string portalUrl, string? token)
    {
        var ok = true;
        void Say(string tag, bool pass, string detail)
        {
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {tag}：{detail}");
            if (!pass) ok = false;
        }

        Console.WriteLine($"MediaRack.Client 连接页自检 → {portalUrl}");
        Console.WriteLine();

        // ── 一、表单规则（不联网）────────────────────────────────
        var vm = new ConnectViewModel((_, _, _, _) => { });

        Say("空表单", !vm.CanConnect, $"CanConnect={vm.CanConnect}（三栏全空应是 false）");

        vm.Host = "127.0.0.1";
        vm.Port = "8080";
        Say("缺源地址", !vm.CanConnect, $"CanConnect={vm.CanConnect}（只填地址+端口仍是 false，这是这次改的核心）");

        vm.SourcePath = "SB";
        Say("三栏齐全", vm.CanConnect, $"CanConnect={vm.CanConnect}，预览 {vm.AddressPreview}");
        Say("地址拼装", vm.AddressPreview == "http://127.0.0.1:8080/s/SB", vm.AddressPreview);

        // 粘进来带 /s/ 前缀或首尾斜杠时不能拼成 /s/s/SB
        vm.SourcePath = "/s/SB/";
        Say("源地址归一化", vm.AddressPreview == "http://127.0.0.1:8080/s/SB", $"输入 /s/SB/ → {vm.AddressPreview}");

        vm.SourcePath = "";
        Say("清空源地址后禁用", !vm.CanConnect, $"CanConnect={vm.CanConnect}");

        // ── 二、地址形态判定 ───────────────────────────────────
        Say("门户地址认得出", ApiClient.IsSourcePortalUrl("http://127.0.0.1:8080/s/SB"), "…/s/SB → true");
        Say("根路径不认", !ApiClient.IsSourcePortalUrl("http://127.0.0.1:8080"), "…:8080 → false");
        Say("缺 slug 不认", !ApiClient.IsSourcePortalUrl("http://127.0.0.1:8080/s/"), "…/s/ → false");

        // ── 三、服务端根路径确实会列出多个源（所以才要在客户端堵）──
        var root = RootOf(portalUrl);
        if (root is not null)
        {
            try
            {
                using var rootApi = new ApiClient(root);
                var all = await rootApi.SourcesAsync();
                Say("根路径全量可见", all.Count > 0,
                    $"本机根路径 {root} 一次列出 {all.Count} 个源 —— 客户端因此一律不认这种地址");
            }
            catch (Exception e) when (e is ApiException or NetworkException)
            {
                Console.WriteLine($"  [跳过] 根路径探测：{e.Message}");
            }
        }

        // ── 四、真连一次门户地址，结果只能有一条源 ────────────────
        var (host, port, path) = ApiClient.SplitSourceBaseUrl(portalUrl);
        var slug = path.Trim('/');
        if (slug.StartsWith("s/", StringComparison.Ordinal)) slug = slug[2..];
        if (host.Length == 0 || port.Length == 0 || slug.Length == 0)
        {
            Console.WriteLine($"  [FAIL] 参数不是源门户地址：{portalUrl}");
            return 1;
        }

        List<SourceInfoDto>? got = null;
        string? gotUrl = null;
        var live = new ConnectViewModel((entry, sources, api, _) =>
        {
            got = sources;
            gotUrl = entry.BaseUrl;
            api.Dispose();
        });
        live.Host = host;
        live.Port = port;
        live.SourcePath = slug;
        live.Token = token ?? "";

        await live.ConnectCommand.ExecuteAsync();

        if (got is null)
        {
            Say("连上门户", false, $"没连上：{live.Status}");
        }
        else
        {
            Say("连上门户", true, $"{gotUrl}");
            Say("只拿到一条源", got.Count == 1, $"{got.Count} 条（源「{got[0].Name}」，{got[0].ItemCount:N0} 条视频）");
            Say("落库是门户地址", ApiClient.IsSourcePortalUrl(gotUrl), $"{gotUrl}");
        }

        // ── 五、主页左栏「改」：四栏预填 + 进编辑态 ──────────────
        var entry = new ServerEntry { Name = "改前的名字", BaseUrl = portalUrl, Token = token };
        var edited = new ConnectViewModel((_, _, _, _) => { }, editing: entry);
        Say("编辑态预填四栏",
            edited.IsEditing && edited.Host == host && edited.Port == port && edited.SourcePath == slug,
            $"IsEditing={edited.IsEditing}，{edited.Host}:{edited.Port} 源「{edited.SourcePath}」");
        Say("编辑态按钮可用", edited.CanConnect, $"CanConnect={edited.CanConnect}");
        Say("编辑态带原名字", edited.EditHint.Contains("改前的名字"), edited.EditHint);

        // 源行右侧那两个按钮挂着命令，且点下去真回到外壳（不是空挂）
        if (got is not null)
        {
            SourceSession? hitEdit = null, hitRemove = null;
            var row = new SourceSession(entry, got, null!, null, _ => { }
                , s => hitEdit = s, s => hitRemove = s);
            row.EditCommand.Execute(null);
            row.RemoveCommand.Execute(null);
            Say("源行的改 / 断开可达",
                ReferenceEquals(hitEdit, row) && ReferenceEquals(hitRemove, row),
                $"改→{(hitEdit is null ? "无" : "外壳")}，✕→{(hitRemove is null ? "无" : "外壳")}");
        }

        // ── 六、收藏夹按源拆开 ─────────────────────────────────
        // 判据：门户列表里不该出现"本源一条都没有、别的源却有"的夹子。
        // 根路径（只对本机开放）就是那份"全库视角"，自检跟它对照才有意义。
        try
        {
            using var portal = new ApiClient(portalUrl, token);
            var mine = await portal.FavoritesAsync();

            var allCount = new Dictionary<string, int>();
            if (root is not null)
            {
                using var rootApi = new ApiClient(root);
                foreach (var f in await rootApi.FavoritesAsync()) allCount[f.Id] = f.ItemCount;
            }

            var leaked = mine.Where(f => f.ItemCount == 0 && allCount.GetValueOrDefault(f.Id) > 0).ToList();
            Say("收藏夹按源拆开", leaked.Count == 0,
                $"门户列出 {mine.Count} 个（全库 {allCount.Count} 个）；" +
                (leaked.Count == 0
                    ? "没有混进别的源的夹子"
                    : "混进了： " + string.Join("、", leaked.Select(f => f.Name))));

            var over = mine.Where(f => f.ItemCount > allCount.GetValueOrDefault(f.Id, f.ItemCount)).ToList();
            Say("计数不超过全库", over.Count == 0, $"{over.Count} 个夹子的计数大于全库同名夹子");
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Console.WriteLine($"  [跳过] 收藏夹口径：{e.Message}");
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "全部通过" : "有失败项");
        return ok ? 0 : 1;
    }

    /// <summary>从门户地址反推服务端根（<c>http://host:port</c>）。</summary>
    private static string? RootOf(string portalUrl)
    {
        var (host, port, _) = ApiClient.SplitSourceBaseUrl(portalUrl);
        return host.Length == 0 || port.Length == 0 ? null : $"http://{host}:{port}";
    }
}
