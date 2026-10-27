using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MediaRack.Client.Tools;
using MediaRack.Client.ViewModels;

namespace MediaRack.Client.Views;

/// <summary>
/// 海报墙视图。三件只有视图才能干的事集中在这儿：
/// 1. 卡片海报的懒加载（图片控件进视口才触发，虚拟化后翻页不会把几百张图全拉一遍）
/// 2. 列数跟随窗口宽度（卡片定宽 188，行是"几列"的构造参数，宽度变了要重排行）
/// 3. 滚到接近底部自动续页
/// </summary>
public partial class HomeView : UserControl
{
    private const double CardWidth = 188;
    private const double CardGap = 14;
    private const double HorizontalPadding = 40;

    private HomeViewModel? _vm;
    private ScrollViewer? _scroll;

    public HomeView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => BindViewModel();
        Loaded += OnLoaded;
        GridList.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);

        // ScrollViewer 在 ListBox 的模板里，只有模板真的套装完才存在。
        // 构造 / DataContext 赋值时都还太早，所以挂 LayoutUpdated，找到就退订。
        GridList.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        AttachScroll();
        if (_scroll is not null) GridList.LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        BindViewModel();
        UpdateColumns(GridList.Bounds.Width);
        AttachScroll();
    }

    private void BindViewModel()
    {
        var vm = DataContext as HomeViewModel;
        if (ReferenceEquals(vm, _vm)) return;

        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
        _vm = vm;

        if (vm is null) return;
        AttachScroll();
    }

    private void AttachScroll()
    {
        if (_scroll is not null) return;
        var sv = GridList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv is null) return;
        _scroll = sv;
        _scroll.ScrollChanged += OnScrollChanged;
        ClientLog.Write($"海报墙滚动钩子已挂上（extent={_scroll.Extent.Height:F0}）");
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null) return;
        var remaining = _scroll.Extent.Height - _scroll.Offset.Y - _scroll.Viewport.Height;

        // 提前一屏开始续：用户滑到底之前下一页已经在路上
        if (remaining >= 700) return;

        if (_vm is { CanLoadMore: true })
        {
            ClientLog.Write($"接近底部 remaining={remaining:F0}（extent={_scroll.Extent.Height:F0}），触发续页");
        }
        _vm?.MaybeLoadMore();
    }

    private void UpdateColumns(double width)
    {
        if (width <= 0) return;
        var usable = width - HorizontalPadding;
        var columns = (int)((usable + CardGap) / (CardWidth + CardGap));
        _vm?.SetColumns(Math.Max(1, columns));
    }

    private void OnPosterLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: MediaCard card } && _vm is not null)
        {
            _ = card.EnsurePosterAsync(_vm.Posters);
        }
    }
}
