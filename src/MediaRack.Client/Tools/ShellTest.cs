using MediaRack.Client.Remote;
using MediaRack.Client.Storage;
using MediaRack.Client.ViewModels;

namespace MediaRack.Client.Tools;

/// <summary>
/// 外壳的无头验收：<c>MediaRack.Client --shelltest</c>。
///
/// 守的是"界面骨架和 Android 对齐"这件事——**不联网**，只在主线程上驱动
/// <see cref="ShellViewModel"/> 的状态机：
///
/// <list type="number">
/// <item>一个源都没有时，落地页是<b>空海报墙</b>（不是连接页），抽屉默认收起；</item>
/// <item>☰ 能开合抽屉；「＋ 添加源」/ 空态按钮打开的是<b>覆盖层</b>，关掉回到原页；</item>
/// <item>连上源后标题跟着视图走，「我的」可进可退（退回的还是同一个 Home 实例）；</item>
/// <item>移除最后一个源 → 回到空态。</item>
/// </list>
///
/// 全部同步执行，且刻意不 await 任何网络：<c>Activate</c> 里首屏加载是
/// <c>Dispatcher.Post</c> 出去的，没有消息循环就不会跑，正好让断言只看结构。
/// </summary>
public static class ShellTest
{
    public static int Run()
    {
        var ok = true;
        void Say(string tag, bool pass, string detail)
        {
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {tag}：{detail}");
            if (!pass) ok = false;
        }

        Console.WriteLine("MediaRack.Client 外壳骨架自检（不联网）");
        Console.WriteLine();

        // ── 一、空态 ─────────────────────────────────────────────
        var shell = new ShellViewModel(savedOverride: new List<ServerEntry>());

        Say("空态落地", shell.IsEmpty, $"CurrentPage={(shell.CurrentPage is null ? "null" : shell.CurrentPage.GetType().Name)}");
        Say("抽屉默认收起", !shell.IsDrawerOpen, $"IsDrawerOpen={shell.IsDrawerOpen}（和手机一致：一进来就是墙）");
        Say("不自动弹连接页", !shell.IsConnectOpen, $"IsConnectOpen={shell.IsConnectOpen}");
        Say("顶栏标题", shell.Title == "MediaRack", shell.Title);
        Say("空态文案", shell.EmptyTitle.Length > 0 && shell.EmptyText.Contains('☰'),
            $"「{shell.EmptyTitle}」/「{shell.EmptyText}」");

        // ── 二、抽屉开合 ─────────────────────────────────────────
        shell.ToggleDrawerCommand.Execute(null);
        Say("☰ 展开", shell.IsDrawerOpen, $"IsDrawerOpen={shell.IsDrawerOpen}");
        shell.ToggleDrawerCommand.Execute(null);
        Say("☰ 再点收起", !shell.IsDrawerOpen, $"IsDrawerOpen={shell.IsDrawerOpen}");

        // ── 三、连接页是覆盖层 ───────────────────────────────────
        shell.AddSourceCommand.Execute(null);
        Say("添加源 → 覆盖层", shell.IsConnectOpen && shell.Connect is not null,
            $"IsConnectOpen={shell.IsConnectOpen}");
        Say("开覆盖层先收抽屉", !shell.IsDrawerOpen, $"IsDrawerOpen={shell.IsDrawerOpen}");
        Say("覆盖层底下还是空墙", shell.IsEmpty, $"IsEmpty={shell.IsEmpty}（关掉要能原样回来）");
        shell.CloseConnectCommand.Execute(null);
        Say("✕ 关掉覆盖层", !shell.IsConnectOpen, $"IsConnectOpen={shell.IsConnectOpen}, Connect={(shell.Connect is null ? "null" : "非空")}");

        // ── 四、连上源之后的骨架（不联网，直接塞一个会话）────────
        var entry = new ServerEntry
        {
            Id = "shelltest",
            Name = "自检源",
            BaseUrl = "http://127.0.0.1:8080/s/SB",
            Token = "125820196",
        };
        var api = new ApiClient(entry.BaseUrl, entry.Token);
        var sources = new List<SourceInfoDto> { new() { Id = "SB", Name = "Sex_B", ItemCount = 2799 } };

        var session = shell.AttachForTest(entry, sources, api, "1.0.0");
        shell.Activate(session);

        Say("切换后不是空态", !shell.IsEmpty, $"CurrentPage={shell.CurrentPage?.GetType().Name}");
        Say("主页归位", shell.Home is not null && ReferenceEquals(shell.Home, shell.CurrentPage), "CurrentPage 即 Home");
        Say("切源顺手收抽屉", !shell.IsDrawerOpen, $"IsDrawerOpen={shell.IsDrawerOpen}");
        Say("标题跟视图", shell.Title == "全部视频", shell.Title);

        var home = shell.Home;

        // ── 五、我的 ↔ 返回 ─────────────────────────────────────
        shell.OpenMineCommand.Execute(null);
        Say("进「我的」", shell.IsMine, $"CurrentPage={shell.CurrentPage?.GetType().Name}");
        Say("我的标题", shell.Title == "我的", shell.Title);
        Say("我的页拿到当前源", shell.Mine is { } mine && mine.Title == "我的媒体库", "我的媒体库");
        shell.CloseMineCommand.Execute(null);
        Say("返回原墙", ReferenceEquals(shell.CurrentPage, home), "退回的是同一个 HomeViewModel（列表位置不丢）");
        Say("返回后标题复原", shell.Title == "全部视频", shell.Title);

        // ── 六、移除最后一个源 → 空态 ───────────────────────────
        shell.RequestRemoveSource(session);
        Say("移除后回空态", shell.IsEmpty, $"IsEmpty={shell.IsEmpty}");
        Say("移除后标题", shell.Title == "MediaRack", shell.Title);
        Say("移除后池子空", shell.Sessions.Count == 0, $"{shell.Sessions.Count} 个会话");
        api.Dispose();

        Console.WriteLine();
        Console.WriteLine(ok ? "全部通过" : "有失败项");
        return ok ? 0 : 1;
    }
}
