using System.Diagnostics;
using MediaRack.Client.Remote;
using MediaRack.Client.Storage;
using MediaRack.Client.ViewModels;

namespace MediaRack.Client.Tools;

/// <summary>
/// 主页（海报墙）的自动化验收：<c>MediaRack.Client --vmtest [baseUrl]</c>
///
/// 界面本身没法用命令行点，但"点了之后该发生什么"全在 ViewModel 里。
/// 这里直接把 HomeViewModel 当成无头对象驱动一遍：切源、换合集、搜索、续页、取海报，
/// 每一步用服务端返回的真实数字对账（这些数字 curl 也能独立复核，不是自己说自己对）。
/// </summary>
public static class VmTest
{
    public static async Task<int> RunAsync(string baseUrl)
    {
        var ok = true;
        void Say(string tag, bool pass, string detail)
        {
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {tag}：{detail}");
            if (!pass) ok = false;
        }

        Console.WriteLine($"MediaRack.Client 主页自检 → {baseUrl}");
        Console.WriteLine();

        using var api = new ApiClient(baseUrl);

        List<SourceInfoDto> sources;
        try
        {
            sources = await api.SourcesAsync();
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Console.WriteLine($"连不上服务端：{e.Message}");
            return 1;
        }

        var entry = new ServerEntry { BaseUrl = baseUrl, Name = baseUrl };
        using var vm = new HomeViewModel(entry, sources, null, api);

        // 首屏：一页 60 条
        await vm.StartAsync();
        var total = vm.Total;
        Say("首屏", vm.LoadedCount == 60 && total > 0,
            $"{vm.CountText}，{vm.Rows.Count} 行 × {Columns(vm)} 列，导航 {vm.SourceNavs.Count} 项（含全部）");

        Say("行切分对齐", vm.Rows.All(r => r.Cards.Count <= Columns(vm)) && Flatten(vm) == vm.LoadedCount,
            $"行内卡片合计 {Flatten(vm)} = 已加载 {vm.LoadedCount}");

        // 续页
        await vm.LoadMoreCommand.ExecuteAsync();
        Say("续页", vm.LoadedCount == 120, $"{vm.CountText}");

        // 列数变化要整表重排，且不能丢卡片
        var before = vm.LoadedCount;
        vm.SetColumns(5);
        Say("改列数", Columns(vm) == 5 && Flatten(vm) == before,
            $"{vm.Rows.Count} 行 × 5 列，卡片仍 {Flatten(vm)} 张");

        vm.SetColumns(3);

        // 切视图：继续观看（recent）。一条配置 = 一个源之后，侧栏不再逐个列源，
        // 所以"切"的是视图而不是源；判据仍是"换了一份列表、条目数跟着变"。
        var viewPick = vm.SourceNavs.First(n => n.IsRecent);
        vm.SelectedSourceNav = viewPick;
        await WaitIdleAsync(vm);
        var recentTotal = vm.Total;
        Say("切视图", vm.SelectedFavoriteNav is null && recentTotal > 0 && recentTotal != total,
            $"{viewPick.Title} → total={recentTotal}（聚合视图 {total}）");

        // 合集下拉：回到「全部视频」，过滤条目最多的那个合集
        vm.SelectedSourceNav = vm.SourceNavs.First(n => n.IsAll);
        await WaitIdleAsync(vm);

        var biggest = vm.Collections.Where(c => c.Id is not null).OrderByDescending(c => c.ItemCount).FirstOrDefault();
        if (biggest is null)
        {
            Say("合集过滤", false, "该源没有合集");
        }
        else
        {
            vm.SelectedCollection = biggest;
            await WaitIdleAsync(vm);
            Say("合集过滤", vm.Total == biggest.ItemCount,
                $"「{biggest.Label}」→ total={vm.Total}（下拉标注 {biggest.ItemCount}）");

            vm.SelectedCollection = vm.Collections[0];
            await WaitIdleAsync(vm);
            Say("退回全部合集", vm.Total == total, $"total={vm.Total}");
        }

        // 搜索
        await vm.SearchNowAsync("1");
        var searched = vm.Total;
        Say("搜索", searched > 0 && searched < total, $"q=1 → total={searched}");

        await vm.SearchNowAsync("");
        await WaitIdleAsync(vm);

        // 收藏夹
        var fav = vm.FavoriteNavs.FirstOrDefault();
        if (fav is null)
        {
            Say("收藏夹", false, "服务端没有收藏夹");
        }
        else
        {
            vm.SelectedFavoriteNav = fav;
            await WaitIdleAsync(vm);
            Say("收藏夹", vm.SelectedSourceNav is null && vm.Total > 0,
                $"{fav.Title}（{fav.Subtitle}）→ total={vm.Total}");
        }

        // 海报：客户端要自己下载解码（不是把 URL 塞给控件就完事）
        var card = vm.Rows.FirstOrDefault()?.Cards.FirstOrDefault(c => c.PosterUrl is not null);
        if (card is null)
        {
            Say("海报解码", false, "首屏没有带封面的卡片");
        }
        else
        {
            var bmp = await vm.Posters.GetAsync(card.PosterUrl!);
            var again = await vm.Posters.GetAsync(card.PosterUrl!);
            Say("海报解码", bmp is not null && bmp.PixelSize.Width > 0,
                bmp is null
                    ? "下载或解码失败"
                    : $"{bmp.PixelSize.Width}×{bmp.PixelSize.Height}，缓存命中={ReferenceEquals(bmp, again)}，共 {vm.Posters.Count} 张");
        }

        // ── 第 5 段：管理面（继续观看 / 合集页 / 收藏夹增删改）────────────

        // 继续观看：服务端 sort=recent（按 last_play_at 倒序、只留位置 > 0 的）
        var recentNav = vm.SourceNavs.First(n => n.IsRecent);
        vm.SelectedSourceNav = recentNav;
        await WaitIdleAsync(vm);
        var recentUi = vm.Total;
        var expectRecent = (await api.LibraryAsync(0, 1, sort: "recent")).Total;
        var withProgress = vm.Rows.SelectMany(r => r.Cards).Count(c => c.HasProgress);
        Say("继续观看", recentUi > 0 && recentUi == expectRecent && withProgress > 0,
            $"total={recentUi}（端点 {expectRecent}），首屏 {withProgress}/{vm.LoadedCount} 条有续播条");

        // 合集网格：导航第三项，955 个合集分 3 列
        var collNav = vm.SourceNavs.First(n => n.IsCollections);
        vm.SelectedSourceNav = collNav;
        await WaitIdleAsync(vm);
        var allCollections = vm.CollectionCards.Count;
        Say("合集网格", vm.ShowCollectionGrid && allCollections > 0 &&
                        vm.CollectionRows.Sum(r => r.Cards.Count) == allCollections,
            $"{allCollections} 个，{vm.CollectionRows.Count} 行 × 3 列，{vm.CollectionCountText}");

        // 过滤（客户端侧，不发请求）
        var pick = vm.CollectionCards[0];
        vm.CollectionQuery = pick.Name;
        var filtered = vm.CollectionCards.Count;
        Say("合集过滤", filtered >= 1 && filtered < allCollections,
            $"「{pick.Name}」→ {filtered} / {allCollections} 个");
        vm.CollectionQuery = "";

        // 点进合集：走单源列表 + collection 过滤，total 要跟卡片标注的条数一致
        vm.OpenCollection(pick);
        await WaitIdleAsync(vm);
        Say("进合集", vm.ShowCollectionBack && vm.Total == pick.ItemCount && !vm.ShowCollectionGrid,
            $"「{pick.Name}」→ total={vm.Total}（卡片标注 {pick.ItemCount}），{vm.Rows.Count} 行");

        vm.BackToCollectionGrid();
        await WaitIdleAsync(vm);
        Say("回合集网格", vm.ShowCollectionGrid && vm.CollectionCards.Count == allCollections,
            $"回到网格，{vm.CollectionCards.Count} 个");

        // ── 收藏夹增删改（真建真删，不留垃圾）──────────────────────────
        var tempName = $"_自检临时{DateTime.Now:HHmmss}";
        string? tempId = null;
        try
        {
            vm.BeginCreateFavoriteCommand.Execute(null);
            vm.NewFavoriteName = tempName;
            await vm.CommitCreateFavoriteCommand.ExecuteAsync();
            tempId = vm.FavoriteNavs.FirstOrDefault(n => n.Title == tempName)?.Id;
            Say("新建收藏夹", tempId is not null, $"{tempName} → 侧栏 {vm.FavoriteNavs.Count} 个");

            // 选一条，等「收藏到」的 chips 加载（异步 fire-and-forget，要等）
            vm.SelectedSourceNav = vm.SourceNavs[0];
            await WaitIdleAsync(vm);
            var target = vm.Rows[0].Cards[0];
            vm.SelectedCard = target;
            await WaitUntilAsync(() => vm.FavoriteChips.Count > 0);

            var chip = vm.FavoriteChips.FirstOrDefault(c => c.Id == tempId);
            Say("收藏归属", chip is not null && !chip.On,
                $"这条在 {vm.FavoriteChips.Count} 个夹子里，临时夹当前={chip?.Mark}");

            if (chip is not null)
            {
                var countBefore = (await api.FavoritesAsync()).First(f => f.Id == tempId).ItemCount;

                chip.ToggleCommand.Execute(chip);
                await WaitUntilAsync(() => chip.On);
                var afterAdd = (await api.FavoritesAsync()).First(f => f.Id == tempId).ItemCount;
                Say("加入收藏夹", chip.On && afterAdd == countBefore + 1,
                    $"chip={chip.Mark}，服务端计数 {countBefore} → {afterAdd}");

                chip.ToggleCommand.Execute(chip);
                await WaitUntilAsync(() => !chip.On);
                var afterRemove = (await api.FavoritesAsync()).First(f => f.Id == tempId).ItemCount;
                Say("移出收藏夹", !chip.On && afterRemove == countBefore,
                    $"chip={chip.Mark}，服务端计数回到 {afterRemove}");
            }

            // 重命名
            var tempNav = vm.FavoriteNavs.First(n => n.Id == tempId);
            vm.SelectedFavoriteNav = tempNav;
            await WaitIdleAsync(vm);
            var renamed = tempName + "改";
            vm.BeginRenameFavoriteCommand.Execute(null);
            vm.RenameText = renamed;
            await vm.CommitRenameFavoriteCommand.ExecuteAsync();
            Say("重命名收藏夹", vm.FavoriteNavs.Any(n => n.Title == renamed),
                $"{tempName} → {renamed}");

            // 删除
            vm.BeginDeleteFavoriteCommand.Execute(null);
            var confirmShown = vm.ConfirmDeleteFavorite;
            await vm.CommitDeleteFavoriteCommand.ExecuteAsync();
            tempId = null; // 删掉了，finally 不用再清
            Say("删除收藏夹", confirmShown && !vm.FavoriteNavs.Any(n => n.Title == renamed),
                $"二次确认={confirmShown}，侧栏剩 {vm.FavoriteNavs.Count} 个，已退回「{vm.SelectedSourceNav?.Title}」");
        }
        catch (Exception e)
        {
            Say("收藏夹增删改", false, e.Message);
        }
        finally
        {
            // 中途失败也别在真库里留东西
            if (tempId is not null)
            {
                try { await api.DeleteFavoriteAsync(tempId); }
                catch { /* 清理失败只能记一笔，不能盖掉原断言结果 */ }
            }
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "全部通过。" : "有失败项，见上。");
        return ok ? 0 : 1;
    }

    private static int Columns(HomeViewModel vm) => vm.Rows.Count > 0 ? vm.Rows[0].Columns : 0;

    private static int Flatten(HomeViewModel vm) => vm.Rows.Sum(r => r.Cards.Count);

    /// <summary>等一个条件成立（异步命令 / fire-and-forget 的加载没有完成信号）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 8_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(60);
        }
        return condition();
    }

    /// <summary>等界面把这一轮加载跑完（含"加载中又改了条件"触发的第二次）。</summary>
    private static async Task WaitIdleAsync(HomeViewModel vm, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(60);
        while (vm.IsLoading && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        // pendingReload 的二次加载在 IsLoading=false 之后才起，再等一小轮
        await Task.Delay(120);
        while (vm.IsLoading && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
    }
}
