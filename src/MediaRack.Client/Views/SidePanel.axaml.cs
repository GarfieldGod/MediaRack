using Avalonia.Controls;

namespace MediaRack.Client.Views;

/// <summary>
/// 左抽屉里那份导航（浏览 / 收藏夹 / 源）。内容全交给 <see cref="ViewModels.HomeViewModel"/>，
/// 这里没有代码——列表选中、命令都在 ViewModel 上。
/// </summary>
public partial class SidePanel : UserControl
{
    public SidePanel()
    {
        InitializeComponent();
    }
}
