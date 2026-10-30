using Avalonia.Controls;
using Avalonia.Interactivity;
using MediaRack.Client.ViewModels;

namespace MediaRack.Client.Views;

/// <summary>
/// 「我的」页视图。唯一需要代码的事是封面懒加载——和海报墙同一个套路：
/// 图片控件进视口才触发 <see cref="MediaCard.EnsurePosterAsync"/>。
/// </summary>
public partial class MineView : UserControl
{
    public MineView()
    {
        InitializeComponent();
    }

    private void OnPosterLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: MediaCard card } && DataContext is MineViewModel vm)
        {
            _ = card.EnsurePosterAsync(vm.Posters);
        }
    }
}
