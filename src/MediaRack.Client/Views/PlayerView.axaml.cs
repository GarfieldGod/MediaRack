using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MediaRack.Client.Tools;
using MediaRack.Client.ViewModels;

namespace MediaRack.Client.Views;

/// <summary>
/// 播放页视图。View 只干四件事：把 ViewModel 里的 <c>MediaPlayer</c> 交给 <c>VideoView</c>、
/// 拖动进度条时先别 seek（松手才 seek）、键盘快捷键、全屏这种"必须碰 Window"的事。
/// 其余状态推进全在 <see cref="PlayerViewModel.Pump"/> 里。
///
/// <para>快捷键与 APK 的手势是一一对应的替代关系：APK 点按显隐 / 双击暂停 / 横滑 seek /
/// 左右竖滑亮度音量，桌面上没有触屏，改成一整套键盘操作（见 <see cref="OnKeyDown"/>）。</para>
/// </summary>
public partial class PlayerView : UserControl
{
    private PlayerViewModel? _vm;
    private TopLevel? _top;
    private bool _fullscreen;
    private WindowState _prevState = WindowState.Normal;

    public PlayerView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Bind();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // 拖动过程中每个像素都 seek 会把 VLC 的 demux 打成一锅粥（画面反复重缓冲），
        // 所以按下时冻结、松手时只落一次
        Seek.PointerPressed += (_, _) => _vm?.BeginScrub();
        Seek.PointerReleased += (_, _) => _vm?.EndScrub();
        Seek.PointerCaptureLost += (_, _) => _vm?.EndScrub();

        // 铺满裁边要知道"视口是什么比例"，这个只有视图知道
        SizeChanged += (_, _) => _vm?.SetViewportSize(Video.Bounds.Width, Video.Bounds.Height);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Bind();
        Focus();
        _vm?.SetViewportSize(Video.Bounds.Width, Video.Bounds.Height);

        // 快捷键挂在 TopLevel 上，而且走**隧道**阶段：
        // 播放页是叠在列表上的一层，下面的搜索框 / 倍速下拉仍持有焦点，
        // 冒泡阶段字母键和方向键会被它们先吃掉（表现为"按 F 没反应"）。
        // 隧道先经过我们，再交给子控件。
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null) _top.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        ClientLog.Write("播放页已装载" + (_vm?.MediaPlayer is null ? "（无引擎）" : "，画面已绑定"));
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_top is not null) _top.RemoveHandler(KeyDownEvent, OnKeyDown);

        // 离开播放页不能把窗口留在全屏：用户会以为整个客户端卡住了
        if (_fullscreen) ToggleFullscreen();

        _top = null;
        Video.MediaPlayer = null;
    }

    private void Bind()
    {
        var vm = DataContext as PlayerViewModel;
        if (ReferenceEquals(vm, _vm)) return;

        if (_vm is not null) _vm.FullscreenRequested -= OnFullscreenRequested;
        _vm = vm;
        if (_vm is not null) _vm.FullscreenRequested += OnFullscreenRequested;

        // VideoView 必须"后赋值"才接得上：它赋值时会订阅画面事件，
        // 已经在播的 MediaPlayer 也能立刻出画面，所以 Loaded 之后再设没问题
        Video.MediaPlayer = vm?.MediaPlayer;
    }

    // ── 全屏 ───────────────────────────────────────────────────────

    private void OnFullscreenRequested(object? sender, EventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_top is not Window w)
        {
            // 无头自检里没有窗口：只让 VM 的状态位跟着走，不碰 Window
            _fullscreen = !_fullscreen;
            _vm?.SetFullscreenState(_fullscreen);
            return;
        }

        _fullscreen = !_fullscreen;

        if (_fullscreen)
        {
            _prevState = w.WindowState;
            w.WindowState = WindowState.FullScreen;
            // 全屏时把两边的信息栏都收掉，画面能多占近百像素；控制条留着（桌面播放器的习惯）
            TopBar.IsVisible = false;
            Chrome(w, false);
        }
        else
        {
            w.WindowState = _prevState == WindowState.FullScreen ? WindowState.Normal : _prevState;
            TopBar.IsVisible = true;
            Chrome(w, true);
        }

        _vm?.SetFullscreenState(_fullscreen);
        ClientLog.Write(_fullscreen ? "进入全屏" : "退出全屏");
    }

    /// <summary>壳顶栏（窗口自己那条 MediaRack 标题栏）跟着全屏一起收放。</summary>
    private static void Chrome(Window w, bool visible)
    {
        if (w is MainWindow mw) mw.SetPlayerFullscreen(!visible);
    }

    // ── 快捷键 ─────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Handled) return;

        // 下拉展开时，上下/回车/字母都归它（选项搜索），别抢
        if (e.Source is ComboBox { IsDropDownOpen: true }) return;

        // ⚠️ 中文输入法会先把字母键吃掉，Avalonia 12 报上来的是 Key.ImeProcessed
        // （12 里没有 ImeProcessedKey，官方建议字母类快捷键按**物理键位**判定）。
        // 不转这一道，F / N / M / L / C 在中文输入法下全部失灵——而空格、ESC、方向键
        // 不受 IME 影响，所以症状是"一部分快捷键好使、一部分没反应"，很容易误判成绑错。
        var key = e.Key;
        if (key is Key.ImeProcessed or Key.None)
            key = FromPhysical(e.PhysicalKey);

        // 焦点在进度条/倍速下拉里时，方向键归它们自己用（微调 / 移动选项）
        var onRange = e.Source is Slider or ComboBox;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (key)
        {
            case Key.Space or Key.K:
                _vm.TogglePlay();
                break;

            case Key.Escape:
                // 全屏时 ESC 先退全屏（和浏览器/播放器的肌肉记忆一致），再按才返回
                if (_fullscreen) ToggleFullscreen();
                else _vm.Close();
                break;

            case Key.Left when !onRange:
                _vm.Skip(shift ? -60_000 : -10_000);
                break;

            case Key.Right when !onRange:
                _vm.Skip(shift ? 60_000 : 10_000);
                break;

            case Key.Up when !onRange:
                _vm.AdjustVolume(5);
                break;

            case Key.Down when !onRange:
                _vm.AdjustVolume(-5);
                break;

            case Key.M:
                _vm.ToggleMute();
                break;

            case Key.F or Key.F11:
                ToggleFullscreen();
                break;

            case Key.N:
                _vm.Next();
                break;

            case Key.P:
                _vm.Prev();
                break;

            case Key.L:
                _vm.Mode = _vm.Mode.Next();
                break;

            case Key.C:
                _vm.ToggleCover();
                break;

            case Key.OemOpenBrackets:
                _vm.SpeedIndex = Math.Max(0, _vm.SpeedIndex - 1);
                break;

            case Key.OemCloseBrackets:
                _vm.SpeedIndex = Math.Min(PlayerViewModel.SpeedSteps.Length - 1, _vm.SpeedIndex + 1);
                break;

            default:
                // 不认识的键照常往子控件传（播放页不吞输入）。
                // 打一行日志：排查"按了没反应"时能立刻看出键到底有没有到播放页。
                if (e.Source is not (TextBox or ComboBox or Slider))
                    ClientLog.Write($"播放页未处理按键 {key}（原始 {e.Key} / 物理 {e.PhysicalKey}，焦点={e.Source?.GetType().Name}）");
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// IME 把字母键吃掉时的兜底：按物理键位认（与键盘布局无关，QWERTY 位置为准）。
    /// 只映射播放页真正用到的键；方向键/空格/ESC 的 <c>Key</c> 本来就是对的，列在这里只是兜底。
    /// </summary>
    private static Key FromPhysical(PhysicalKey physical) => physical switch
    {
        PhysicalKey.A => Key.A,
        PhysicalKey.C => Key.C,
        PhysicalKey.F => Key.F,
        PhysicalKey.K => Key.K,
        PhysicalKey.L => Key.L,
        PhysicalKey.M => Key.M,
        PhysicalKey.N => Key.N,
        PhysicalKey.P => Key.P,
        PhysicalKey.Space => Key.Space,
        PhysicalKey.Escape => Key.Escape,
        PhysicalKey.ArrowLeft => Key.Left,
        PhysicalKey.ArrowRight => Key.Right,
        PhysicalKey.ArrowUp => Key.Up,
        PhysicalKey.ArrowDown => Key.Down,
        PhysicalKey.BracketLeft => Key.OemOpenBrackets,
        PhysicalKey.BracketRight => Key.OemCloseBrackets,
        PhysicalKey.F11 => Key.F11,
        _ => Key.None,
    };
}
