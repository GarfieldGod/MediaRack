using MediaRack.Client.Remote;
using MediaRack.Client.Storage;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 一个**活着的源**：一条连接配置 + 它自己的 <see cref="ApiClient"/>。
///
/// 桌面端可以同时连任意多个源（对应 Android 抽屉里的源列表）：每个源一个会话，
/// 各有各的令牌、各有各的收藏夹与库；切源只是换当前会话，不重新探活、也不断开别的源。
/// </summary>
public sealed class SourceSession : ViewModelBase
{
    private bool _isCurrent;
    private string _title;

    public SourceSession(ServerEntry entry, List<SourceInfoDto> sources, ApiClient api, string? serverVersion,
        Action<SourceSession> onActivate, Action<SourceSession>? onEdit = null, Action<SourceSession>? onRemove = null)
    {
        Entry = entry;
        Sources = sources;
        Api = api;
        ServerVersion = serverVersion;
        ActivateCommand = new RelayCommand(() => onActivate(this));
        EditCommand = new RelayCommand(() => onEdit?.Invoke(this), () => onEdit is not null);
        RemoveCommand = new RelayCommand(() => onRemove?.Invoke(this), () => onRemove is not null);

        // 门户恒给一条源，它的名字就是用户给这个源起的名字（「电影」「番剧」…）
        var sourceName = sources.Count > 0 ? sources[0].Name : "";
        _title = FirstNonEmpty(entry.Name, sourceName, entry.AddressLine);
        ItemCount = sources.Sum(s => s.ItemCount);
    }

    public ServerEntry Entry { get; }
    public List<SourceInfoDto> Sources { get; }
    public ApiClient Api { get; }
    public string? ServerVersion { get; }

    /// <summary>左栏那行标题。改名（编辑后重连）会换掉同一个会话对象，所以要可变。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value)) OnPropertyChanged(nameof(Label));
        }
    }

    /// <summary>这个源的条目总数（左栏第二行）。</summary>
    public int ItemCount { get; }

    /// <summary>列表第二行：<c>host:port/s/slug · 1.2 万条</c>。</summary>
    public string Subtitle => $"{Entry.AddressLine} · {ItemCount:N0} 条";

    /// <summary>当前正在浏览的那个源（左栏高亮用）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    /// <summary>左栏点这一行 = 切到这个源（命令挂在行上，视图不用往上游找 ViewModel）。</summary>
    public RelayCommand ActivateCommand { get; }

    /// <summary>行右侧的「改」：进连接页并预填这条的地址与令牌，改完重连即替换这个会话。</summary>
    public RelayCommand EditCommand { get; }

    /// <summary>行右侧的「✕」：断开并从列表移除。<b>不动配置文件</b>，连接页仍能重连。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>顶栏那行服务器信息：源名 + 地址 + 版本。</summary>
    public string Label =>
        ServerVersion is { Length: > 0 } v ? $"{Title} · {Entry.AddressLine} · v{v}" : $"{Title} · {Entry.AddressLine}";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
