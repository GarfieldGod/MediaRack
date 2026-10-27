namespace MediaRack.Client.ViewModels;

/// <summary>
/// 详情侧栏里的一个收藏夹开关（chip）。
///
/// 用按钮 + 文案标记而不是 CheckBox：CheckBox 点一下会先自己改 <c>IsChecked</c>，
/// 再被绑定推回来，一次点击要跟服务端来回一次才定状态，网络慢的时候会看到勾选先跳再弹回。
/// 这里唯一的状态来源是服务端：请求成功才翻转 <see cref="On"/>。
/// </summary>
public sealed class FavoriteChip : ViewModelBase
{
    private bool _on;

    public FavoriteChip(string id, string name, bool on, Func<FavoriteChip, Task> onToggle)
    {
        Id = id;
        Name = name;
        _on = on;
        ToggleCommand = new RelayCommand<FavoriteChip>(c => _ = onToggle(c));
    }

    public string Id { get; }
    public string Name { get; }

    public bool On
    {
        get => _on;
        set
        {
            if (!Set(ref _on, value)) return;
            OnPropertyChanged(nameof(Mark));
            OnPropertyChanged(nameof(Tip));
        }
    }

    /// <summary>按钮上那个符号。用 ✓/＋ 而不是换个颜色：色觉障碍与截图灰度下都还分得清。</summary>
    public string Mark => _on ? "✓" : "＋";

    public string Tip => _on ? $"从「{Name}」移出" : $"加入「{Name}」";

    public RelayCommand<FavoriteChip> ToggleCommand { get; }
}
