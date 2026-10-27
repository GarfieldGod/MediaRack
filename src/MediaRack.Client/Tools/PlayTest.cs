using System.Diagnostics;
using MediaRack.Client.Playback;
using MediaRack.Client.Remote;
using MediaRack.Client.Storage;
using MediaRack.Client.ViewModels;

namespace MediaRack.Client.Tools;

/// <summary>
/// 播放链路的自动化验收：<c>MediaRack.Client --playtest [baseUrl]</c>
///
/// 这里验的是"点了播放之后真的会发生什么"，而不是接口能不能通：
/// libvlc 起不起得来、双流合成有没有出画面、位置有没有前进、关掉时进度有没有写回服务端、
/// 下次打开会不会从该处续播。全程用 <c>--vout=dummy</c>（不建窗口、不出画面）跑，
/// 但解码是真的走，所以 demux/解码出问题这里就会红。
/// </summary>
public static class PlayTest
{
    public static async Task<int> RunAsync(string baseUrl)
    {
        var ok = true;
        void Say(string tag, bool pass, string detail)
        {
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {tag}：{detail}");
            if (!pass) ok = false;
        }

        Console.WriteLine($"MediaRack.Client 播放自检 → {baseUrl}");
        Console.WriteLine();

        // 引擎必须是第一个初始化的东西：LibVLC 单例的参数只在第一次调用时生效
        var libvlc = PlaybackEngine.Get(["--vout=dummy", "--aout=dummy", "--no-osd", "--no-video-title-show"]);
        Say("libvlc 加载", libvlc is not null,
            libvlc is null
                ? "失败：" + PlaybackEngine.Error
                : $"{libvlc.Version}，native={PlaybackEngine.NativeDirectory ?? "系统默认"}");

        if (libvlc is null) return 1;

        using var api = new ApiClient(baseUrl);

        MediaSummaryDto? dual, single, small;
        try
        {
            dual = await PickAsync(api, "BILIBILI", d => d.Layout == "dual" && d.DurationMs > 120_000);
            single = await PickAsync(api, "H", d => d.Layout == "single" && d.DurationMs > 120_000);
            small = await PickAsync(api, "BILIBILI", d => d.Layout == "dual" && d.DurationMs is > 5_000 and < 30_000);
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Console.WriteLine($"连不上服务端：{e.Message}");
            return 1;
        }

        if (dual is null || single is null)
        {
            Console.WriteLine("库里找不到合适的测例（需要一条 dual 长视频 + 一条 single 长视频）");
            return 1;
        }

        // 测试会写播放进度，先把原值抄下来，结束时还回去——别拿真库当草稿纸
        long origDual, origSingle;
        try
        {
            origDual = (await api.MediaDetailAsync(dual.SourceId, dual.Key)).Meta?.PlayPosMs ?? 0;
            origSingle = (await api.MediaDetailAsync(single.SourceId, single.Key)).Meta?.PlayPosMs ?? 0;
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Console.WriteLine($"读测例详情失败：{e.Message}");
            return 1;
        }
        Console.WriteLine($"测例：dual={dual.Key}（{dual.DurationMs / 1000}s，原进度 {origDual}ms）");
        Console.WriteLine($"      single={single.Key}（{single.DurationMs / 1000}s，原进度 {origSingle}ms）");
        Console.WriteLine();

        using var player = new PlayerViewModel(api);

        try
        {
            // ── 1. 双流直连起播 ────────────────────────────────
            await player.OpenAsync(dual.SourceId, dual.Key, dual.Title, dual.SourceId);
            var started = PumpUntil(player, () => player.PositionMs > 800, 25_000);

            Say("双流起播", started && player.EngineLabel.Contains("双流"),
                player.HasError
                    ? "错误：" + player.Error
                    : $"引擎={player.EngineLabel}，位置 {player.PositionMs}ms 时长 {player.DurationMs}ms");

            Say("时长对齐", Math.Abs(player.DurationMs - dual.DurationMs) < 2_000,
                $"引擎 {player.DurationMs}ms vs 服务端 {dual.DurationMs}ms");

            Say("可 seek", player.IsSeekable, $"IsSeekable={player.IsSeekable}");

            // ── 2. 快进 10 秒真的生效 ──────────────────────────
            var before = player.PositionMs;
            player.Skip(10_000);
            var seeked = PumpUntil(player, () => player.PositionMs >= before + 8_000, 12_000);
            Say("快进 10s", seeked, $"{before}ms → {player.PositionMs}ms");

            // ── 3. 关闭时把进度写回服务端 ──────────────────────
            var closedAt = player.PositionMs;
            player.Close();
            await player.LastReport;
            await Task.Delay(150);

            var afterClose = (await api.MediaDetailAsync(dual.SourceId, dual.Key)).Meta?.PlayPosMs ?? -1;
            Say("进度写回", Math.Abs(afterClose - closedAt) < 3_000,
                $"客户端停在 {closedAt}ms，服务端 playPosMs={afterClose}ms");

            // ── 4. 续播：游标在服务端，换台机器也能接上 ────────
            await api.ReportProgressAsync(dual.SourceId, dual.Key, 60_000);
            await player.OpenAsync(dual.SourceId, dual.Key, dual.Title, dual.SourceId);
            var resumed = PumpUntil(player, () => player.PositionMs >= 58_000, 25_000);
            Say("续播", resumed && player.HasHint,
                player.HasError ? "错误：" + player.Error
                    : $"提示「{player.Hint}」，实际位置 {player.PositionMs}ms（写入 60000ms）");

            player.Close();
            await player.LastReport;

            // ── 5. 看完即归零：下次不该从片尾接着播 ────────────
            await api.ReportProgressAsync(dual.SourceId, dual.Key, dual.DurationMs - 3_000, completed: true);
            var afterComplete = (await api.MediaDetailAsync(dual.SourceId, dual.Key)).Meta?.PlayPosMs ?? -1;
            Say("看完归零", afterComplete == 0, $"completed=true 后 playPosMs={afterComplete}（应为 0）");

            // ── 6. 单文件（single）起播 ────────────────────────
            await player.OpenAsync(single.SourceId, single.Key, single.Title, single.SourceId);
            var singleStarted = PumpUntil(player, () => player.PositionMs > 800, 25_000);
            Say("单文件起播", singleStarted && player.EngineLabel.Contains("单文件"),
                player.HasError
                    ? "错误：" + player.Error
                    : $"引擎={player.EngineLabel}，位置 {player.PositionMs}ms 时长 {player.DurationMs}ms");

            var singleAt = player.PositionMs;
            player.Close();
            await player.LastReport;
            await Task.Delay(150);
            var singleSaved = (await api.MediaDetailAsync(single.SourceId, single.Key)).Meta?.PlayPosMs ?? -1;
            Say("单文件进度写回", Math.Abs(singleSaved - singleAt) < 3_000,
                $"客户端 {singleAt}ms，服务端 {singleSaved}ms");

            // ── 7. 转封装通道（直连坏掉时的兜底）───────────────
            if (small is not null)
            {
                var remuxUrl = api.RemuxUrl(small.SourceId, small.Key);
                var ready = await api.RemuxReadyAsync(remuxUrl, 120_000);
                Say("转封装可用", ready, ready ? "202→200，服务端 ffmpeg 就绪" : "超时或失败");
            }
            else
            {
                Say("转封装可用", false, "库里没有短 dual 条目可测");
            }

            // ── 8. 关页面只更新那一张卡片（不整表重载，滚动位置不丢）──
            await api.ReportProgressAsync(dual.SourceId, dual.Key, 40_000);

            var entry = new ServerEntry { BaseUrl = baseUrl, Name = baseUrl };
            using var home = new HomeViewModel(entry, await api.SourcesAsync(), null, api);
            await home.StartAsync();

            // 聚合视图里不一定有这条，切到它所在的源再找
            var nav = home.SourceNavs.FirstOrDefault(n => n.Id == dual.SourceId);
            if (nav is not null)
            {
                home.SelectedSourceNav = nav;
                await WaitIdleAsync(home);
            }

            var card = home.Rows.SelectMany(r => r.Cards).FirstOrDefault(c => c.Key == dual.Key);
            if (card is null)
            {
                Say("列表回写", false, $"{dual.SourceId} 列表里找不到 {dual.Key}");
            }
            else
            {
                home.PlayCommand.Execute(card);
                var hp = home.Player;
                var played = hp is not null && PumpUntil(hp, () => hp.PositionMs > 40_000, 25_000);
                hp?.Close();

                Say("列表回写", played && card.HasProgress,
                    played
                        ? $"卡片续播条：{card.ProgressText}（{card.ProgressPercent:F1}%）"
                        : "列表里那条没能起播");
            }

            // ── 9. 连播队列：上一个 / 下一个（APK 的 PlayQueue 语义）──
            var queue = new List<PlayQueueItem>
            {
                new(dual.SourceId, dual.Key, dual.Title, dual.SourceId),
                new(single.SourceId, single.Key, single.Title, single.SourceId),
                new(single.SourceId, single.Key, single.Title, single.SourceId),
            };

            await player.PlayAsync(queue, 0, "连播：自检");
            var qStart = PumpUntil(player, () => player.PositionMs > 800, 25_000);
            Say("队列起播", qStart && player.QueueText.Contains("1/3") && !player.HasPrev && player.HasNext,
                player.HasError ? "错误：" + player.Error
                    : $"「{player.QueueText}」，HasPrev={player.HasPrev}，HasNext={player.HasNext}");

            player.Next();
            var qNext = PumpUntil(player, () => player.QueueIndex == 1 && player.PositionMs > 800, 25_000);
            Say("下一个", qNext && player.HasPrev && player.HasNext,
                $"队列位置={player.QueueIndex + 1}/{player.QueueCount}，key={player.CurrentKey}，" +
                $"位置 {player.PositionMs}ms");

            player.Prev();
            var qPrev = PumpUntil(player, () => player.QueueIndex == 0 && player.PositionMs > 800, 25_000);
            Say("上一个", qPrev && !player.HasPrev,
                $"队列位置={player.QueueIndex + 1}/{player.QueueCount}，HasPrev={player.HasPrev}");

            // ── 10. 倍速 / 静音：改的是引擎，不只是界面数字 ────────
            player.SetSpeed(2.0);
            Thread.Sleep(120);
            player.Pump();
            Say("倍速 2.0×", Math.Abs(player.EngineRate - 2.0) < 0.05,
                $"引擎 rate={player.EngineRate:F2}，界面={player.SpeedText}，选中项={player.SpeedIndex}");

            player.SetSpeed(1.0);
            Thread.Sleep(80);
            player.Pump();

            // 注意：--aout=dummy 下引擎不回报音量（读到恒 0），所以这里验的是状态机：
            // 静音不动音量滑块的位置、按钮跟着翻、再点一次能回来。引擎侧靠 GUI 人耳验。
            var volBefore = player.Volume;
            player.ToggleMute();
            Thread.Sleep(80);
            Say("静音", player.IsMuted && Math.Abs(player.Volume - volBefore) < 0.01,
                $"按钮={player.MuteText}，音量滑块仍在 {player.Volume}，引擎读回={player.EngineVolume}（dummy 不可信）");
            player.ToggleMute();
            Thread.Sleep(80);
            Say("取消静音", !player.IsMuted && Math.Abs(player.Volume - volBefore) < 0.01,
                $"按钮={player.MuteText}，音量 {player.Volume}");

            // ── 11. 播放模式三态 + 播完决策（纯函数，可以脱离播放器验）──
            var m0 = PlayMode.PauseAtEnd;
            var m1 = m0.Next();
            var m2 = m1.Next();
            var m3 = m2.Next();
            Say("模式循环", m1 == PlayMode.LoopOne && m2 == PlayMode.AutoNext && m3 == PlayMode.PauseAtEnd,
                $"{m0.Label()} → {m1.Label()} → {m2.Label()} → {m3.Label()}");

            var decideOk =
                PlayMode.PauseAtEnd.DecideEnd(true) == EndAction.Stop &&
                PlayMode.LoopOne.DecideEnd(false) == EndAction.Replay &&
                PlayMode.AutoNext.DecideEnd(true) == EndAction.Next &&
                PlayMode.AutoNext.DecideEnd(false) == EndAction.Stop;
            Say("播完决策", decideOk,
                "播完暂停→停；循环→重播；自动切集且有下一条→下一条；最后一条→停");

            // 命令层面也点一遍：CycleModeCommand 必须真的改 Mode（按钮绑的是它）
            player.Mode = PlayMode.PauseAtEnd;
            player.CycleModeCommand.Execute(null);
            Say("模式按钮", player.Mode == PlayMode.LoopOne && player.ModeText == "循环播放",
                $"点一次 → {player.ModeText}");
            player.Mode = PlayMode.PauseAtEnd;

            // ── 12. 画面适配 / 全屏（全屏只发意图，窗口归视图）──────
            player.SetViewportSize(1600, 900);
            player.ToggleCover();
            Say("铺满裁边", player.Cover && player.FitText == "铺满", $"按钮文本={player.FitText}");
            player.ToggleCover();
            Say("恢复适应", !player.Cover && player.FitText == "适应", $"按钮文本={player.FitText}");

            var fsRequested = 0;
            void OnFs(object? s, EventArgs e) => fsRequested++;
            player.FullscreenRequested += OnFs;
            player.ToggleFullscreenCommand.Execute(null);
            player.FullscreenRequested -= OnFs;
            Say("全屏意图", fsRequested == 1, $"事件触发 {fsRequested} 次（窗口状态由视图处理）");

            player.Close();
            await player.LastReport;
            await Task.Delay(150);
        }
        finally
        {
            // 不管上面哪步炸了，都把测例的进度还原
            try
            {
                await api.ReportProgressAsync(dual.SourceId, dual.Key, origDual);
                await api.ReportProgressAsync(single.SourceId, single.Key, origSingle);
                var back = (await api.MediaDetailAsync(dual.SourceId, dual.Key)).Meta?.PlayPosMs;
                Console.WriteLine();
                Console.WriteLine($"已还原测例进度：dual={back}ms（原 {origDual}）");
            }
            catch (Exception e)
            {
                Console.WriteLine("还原进度失败（手工改回即可）：" + e.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "全部通过。" : "有失败项，见上。");
        return ok ? 0 : 1;
    }

    /// <summary>等列表这一轮加载跑完（切源触发的那次是 fire-and-forget）。</summary>
    private static async Task WaitIdleAsync(HomeViewModel vm, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(80);
        while (vm.IsLoading && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        await Task.Delay(150);
        while (vm.IsLoading && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
    }

    private static async Task<MediaSummaryDto?> PickAsync(
        ApiClient api, string sourceId, Func<MediaSummaryDto, bool> predicate)
    {
        MediaPageDto page;
        try { page = await api.SourceLibraryAsync(sourceId, 0, 100); }
        catch (Exception e) when (e is ApiException or NetworkException) { return null; }
        return page.Items.FirstOrDefault(predicate);
    }

    /// <summary>
    /// 无头驱动：UI 定时器在没有消息循环时不会 Tick，这里自己按 100ms 一拍地推。
    /// 每拍读的都是 VLC 的原子属性，所以和真实运行时的状态推进是同一套代码。
    /// </summary>
    private static bool PumpUntil(PlayerViewModel vm, Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            vm.Pump();
            if (condition()) return true;
            if (vm.HasError) return false;
            Thread.Sleep(100);
        }
        return false;
    }
}
