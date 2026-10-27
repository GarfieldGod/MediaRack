using Avalonia.Threading;
using MediaRack.Client.Remote;
using MediaRack.Client.Storage;
using MediaRack.Client.Tools;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 窗口外壳：顶部一条连接信息栏 + 内容区在「连接页 / 主页」之间切换。
///
/// 启动时自动重连上次用过的服务端（<see cref="ConnectionStore"/>），
/// 连不上才落到连接页——日常使用不该每次都输地址。
/// </summary>
public sealed class ShellViewModel : ViewModelBase
{
    private object? _currentPage;
    private string _serverLabel = "未连接";
    private ApiClient? _api;

    public ShellViewModel()
    {
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);

        // --server：临时连命令行指定的那台（不读不写"上次用的"）
        if (Program.StartupServer is { Length: > 0 } forced)
        {
            var url = ApiClient.NormalizeBaseUrl(forced) ?? forced;
            ClientLog.Write($"命令行指定服务端：{url}");
            var entry = new ServerEntry { BaseUrl = url, Name = url };
            Dispatcher.UIThread.Post(() => _ = ResumeAsync(entry, persist: false));
            return;
        }

        var saved = ConnectionStore.Load();
        ClientLog.Write($"启动：读到 {saved.Count} 条已保存连接（{ConnectionStore.FilePath}）");
        var last = saved.FirstOrDefault();
        if (last is not null)
        {
            // 必须在 UI 消息循环里跑：窗口构造阶段 await 之后不保证回到 UI 线程，
            // 直接在构造里 `_ = ResumeAsync()` 会在后台线程改 UI 属性 →
            // 抛异常且被 Task 静默吞掉（表现为"窗口起来了但停在连接页"）。
            Dispatcher.UIThread.Post(() => _ = ResumeAsync(last));
        }
        else GoConnect(null);
    }

    public object? CurrentPage
    {
        get => _currentPage;
        set => Set(ref _currentPage, value);
    }

    public string ServerLabel
    {
        get => _serverLabel;
        set => Set(ref _serverLabel, value);
    }

    public bool IsConnected => _api is not null;

    public AsyncRelayCommand DisconnectCommand { get; }

    /// <summary>UI 状态只能在 UI 线程改；自动重连的回调可能来自后台线程。</summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void GoConnect(string? notice, bool isError = false) => OnUi(() =>
    {
        (CurrentPage as IDisposable)?.Dispose();
        _api?.Dispose();
        _api = null;
        ServerLabel = "未连接";
        OnPropertyChanged(nameof(IsConnected));
        DisconnectCommand.RaiseCanExecuteChanged();
        CurrentPage = new ConnectViewModel(OnConnected, notice, isError);
    });

    private async Task ResumeAsync(ServerEntry entry, bool persist = true)
    {
        ClientLog.Write($"resume 开始：{entry.BaseUrl} token={(entry.Token is null ? "无" : "有")}");

        // 自动重连用短超时：连不上要赶紧给人看连接页，不能让他对着空白窗口等 20 秒
        var api = new ApiClient(entry.BaseUrl, entry.Token, TimeSpan.FromSeconds(4));
        try
        {
            var auth = await api.AuthStateAsync().ConfigureAwait(true);
            if (auth.AuthRequired && !auth.Authenticated)
            {
                api.Dispose();
                GoConnect($"{entry.DisplayName} 的令牌已失效，请重新扫码", isError: true);
                return;
            }
            string? version = null;
            try { version = (await api.HealthAsync().ConfigureAwait(true)).Version; } catch { /* 非必需 */ }

            var sources = await api.SourcesAsync().ConfigureAwait(true);
            ClientLog.Write($"resume 成功：{sources.Count} 个源，version={version}");
            if (persist) ConnectionStore.Upsert(entry.BaseUrl, entry.Token);
            ApplyConnected(entry, sources, api, version);
        }
        catch (Exception e)
        {
            // 全捕获：这里的异常如果冒泡出去，会被 `_ =` 吞掉，表现为"什么都没发生"
            ClientLog.Write($"resume 失败：{e}");
            api.Dispose();
            GoConnect(e is ApiException or NetworkException
                ? $"{entry.DisplayName} 现在连不上（{e.Message}）"
                : $"{entry.DisplayName} 自动重连失败：{e.Message}", isError: true);
        }
    }

    private void OnConnected(ServerEntry entry, List<SourceInfoDto> sources, ApiClient api) =>
        ApplyConnected(entry, sources, api, null);

    private void ApplyConnected(ServerEntry entry, List<SourceInfoDto> sources, ApiClient api, string? version) => OnUi(() =>
    {
        (CurrentPage as IDisposable)?.Dispose();
        _api?.Dispose();
        _api = api;
        ServerLabel = entry.DisplayName;
        OnPropertyChanged(nameof(IsConnected));
        DisconnectCommand.RaiseCanExecuteChanged();

        var home = new HomeViewModel(entry, sources, version, api);
        CurrentPage = home;

        // 首屏数据（收藏夹 / 合集 / 第一页海报）必须在 UI 消息循环里发起：
        // 构造阶段直接 await 不保证回到 UI 线程，改 UI 属性会抛并被静默吞掉。
        var autoKey = Program.AutoPlayKey;
        if (autoKey is null) Dispatcher.UIThread.Post(() => _ = home.StartAsync());
        else Dispatcher.UIThread.Post(() => _ = home.AutoPlayAsync(autoKey));
    });

    private Task DisconnectAsync()
    {
        GoConnect(null);
        return Task.CompletedTask;
    }
}
