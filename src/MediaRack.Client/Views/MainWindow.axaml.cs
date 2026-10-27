using Avalonia.Controls;
using MediaRack.Client.ViewModels;

namespace MediaRack.Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "MediaRack";
        DataContext = new ShellViewModel();
    }

    /// <summary>
    /// 播放页切全屏时把壳顶栏一起收掉——否则"全屏"里还顶着一条 MediaRack 标题栏，
    /// 画面白白少 46px。由 <see cref="PlayerView"/> 在进出全屏时调用。
    /// </summary>
    public void SetPlayerFullscreen(bool on) => Chrome.IsVisible = !on;
}
