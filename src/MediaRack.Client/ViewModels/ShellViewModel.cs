using System.Collections.ObjectModel;
using Avalonia.Threading;
using MediaRack.Client.Remote;
using MediaRack.Client.Storage;
using MediaRack.Client.Tools;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 主页要用到的「这台机器上还连着哪些源」。由 <see cref="ShellViewModel"/> 实现，
/// 主页只负责把列表画在左栏、把点击转成一次切换——源的生死归壳管。
/// </summary>
public interface ISessionHost
{
    ObservableCollection<SourceSession> Sessions { get; }
    void Activate(SourceSession session);
    void RequestAddSource();

    /// <summary>编辑某个已连着的源：进连接页并预填它的四栏，改完重连即替换这个会话。</summary>
    void RequestEditSource(SourceSession session);

    /// <summary>把某个源从会话池移除（断开）。配置保留，连接页还能重连。</summary>
    void RequestRemoveSource(SourceSession session);
}

/// <summary>
/// 窗口外壳：顶部一条连接信息栏 + 内容区在「连接页 / 主页」之间切换。
///
/// <b>同时可以连多个源</b>（与 Android 抽屉里那份源列表同一个模型）：每个源一个
/// <see cref="SourceSession"/>，自带 ApiClient；启动时并发连全部已保存的源，
/// 切源只是换当前页，不断开别的源，只有「断开当前」才真的释放。
/// </summary>
public sealed class ShellViewModel : ViewModelBase, ISessionHost
{
    private readonly ObservableCollection<SourceSession> _sessions = new();
    private object? _currentPage;
    private string _serverLabel = "未连接";
    private SourceSession? _current;
    private bool _autoPlayConsumed;

    public ShellViewModel()
    {
        DisconnectCommand = new AsyncRelayCommand(DisconnectCurrentAsync, () => _current is not null);
        SwitchSessionCommand = new RelayCommand<SourceSession>(Activate);
        AddSourceCommand = new RelayCommand(RequestAddSource);

        // --server：临时连命令行指定的那台（不读不写"上次用的"）
        if (Program.StartupServer is { Length: > 0 } forced)
        {
            var url = ApiClient.NormalizeBaseUrl(forced) ?? forced;
            ClientLog.Write($"命令行指定服务端：{url}");
            var entry = new ServerEntry { BaseUrl = url, Name = url };
            Dispatcher.UIThread.Post(() => _ = ResumeSingleAsync(entry, persist: false));
            return;
        }

        var saved = ConnectionStore.Load();
        ClientLog.Write($"启动：读到 {saved.Count} 条已保存连接（{ConnectionStore.FilePath}）");
        if (saved.Count == 0) GoConnect(null);
        else Dispatcher.UIThread.Post(() => _ = ResumeAllAsync(saved));
    }

    public ObservableCollection<SourceSession> Sessions => _sessions;

    public RelayCommand<SourceSession> SwitchSessionCommand { get; }
    public RelayCommand AddSourceCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }

    public object? CurrentPage
    {
        get => _currentPage;
        set => Set(ref _currentPage, value);
    }

    public SourceSession? Current => _current;

    public bool HasSessions => _sessions.Count > 0;

    public string ServerLabel
    {
        get => _serverLabel;
        set => Set(ref _serverLabel, value);
    }

    public bool IsConnected => _current is not null;

    /// <summary>UI 状态只能在 UI 线程改；自动重连的回调可能来自后台线程。</summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ── 页面切换 ───────────────────────────────────────────────────

    /// <summary>
    /// 回到连接页。已连着的源**不释放**——「添加源」和「连接失败」都走这条路，
    /// 顺手把别的源断掉等于让用户白连一次。
    /// </summary>
    /// <param name="editing">非空 = 从某个已连着的源点「改」进来的，四栏预填成它的值。</param>
    private void GoConnect(string? notice, bool isError = false, SourceSession? editing = null) => OnUi(() =>
    {
        (CurrentPage as IDisposable)?.Dispose();
        _editingSession = editing;
        CurrentPage = new ConnectViewModel(OnConnected, notice, isError,
            // 已连着的配置在列表里标「已连接」，不给重复连
            baseUrl => _sessions.Any(s =>
                string.Equals(s.Entry.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)),
            editing?.Entry);
    });

    // ── 连接 ───────────────────────────────────────────────────────

    /// <summary>连接页点「连接」成功后回来：入池并立刻切过去（不切就会停在"正在连接…"）。</summary>
    private void OnConnected(ServerEntry entry, List<SourceInfoDto> sources, ApiClient api, string? version) =>
        OnUi(() =>
        {
            // 从「改」进来的：地址可能被改过，旧会话按 baseUrl 认不出来，得主动拔掉
            var replacing = _editingSession;
            _editingSession = null;
            Activate(AddSession(entry, sources, api, version, replacing));
        });

    /// <summary>启动时并发连全部已保存的源——快的那台先上屏，慢的不拖住启动。</summary>
    private async Task ResumeAllAsync(List<ServerEntry> saved)
    {
        ClientLog.Write($"启动并发连接 {saved.Count} 个源");
        OnUi(() => ServerLabel = $"正在连接 {saved.Count} 个源…");

        var tasks = saved.Select(e => TryConnectAsync(e, persist: true)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(true);

        var ok = results.Where(r => r.Session is not null).Select(r => r.Session!).ToList();
        ClientLog.Write($"启动连接结果：成功 {ok.Count} / {saved.Count}");

        if (ok.Count == 0)
        {
            // 一个都没连上，顶栏的"正在连接…"必须收掉，否则会一直挂着
            OnUi(() => ServerLabel = "未连接");

            // 旧版配置只存了服务器地址，没有源地址。这类失败得单独说：
            // 否则用户看到"连不上"会以为服务端没开，其实是配置要补一格。
            var legacy = saved.Count(e => !ApiClient.IsSourcePortalUrl(e.BaseUrl));
            if (legacy == saved.Count)
            {
                GoConnect($"已保存的 {legacy} 条配置都是旧式地址（只有服务器地址、缺源地址）。编辑其中一条补上即可——一条配置只连一个源", isError: true);
                return;
            }

            var why = results.Select(r => r.Error).FirstOrDefault(m => m is { Length: > 0 });
            GoConnect(why is { Length: > 0 } ? $"已保存的源都连不上（{why}）" : "已保存的源都连不上，检查服务端是否开着", isError: true);
            return;
        }

        foreach (var s in ok) AddSessionInternal(s);

        // 当前源 = 上次用的那台（列表按 lastUsedAt 倒序，第一个成功的就是它）
        var pick = ok.FirstOrDefault(s => saved.FirstOrDefault()?.BaseUrl == s.Entry.BaseUrl) ?? ok[0];
        Activate(pick);
    }

    /// <summary>连一台（<c>--server</c> 指定时走这条，不用等"全部已保存"）。</summary>
    private async Task ResumeSingleAsync(ServerEntry entry, bool persist)
    {
        var r = await TryConnectAsync(entry, persist).ConfigureAwait(true);
        if (r.Session is not null) OnUi(() =>
        {
            AddSessionInternal(r.Session);
            Activate(r.Session);
        });
        else GoConnect($"{entry.DisplayName} 连不上（{r.Error}）", isError: true);
    }

    /// <summary>一次连接尝试。异常全部收成 <c>Error</c>，绝不冒泡（否则会被 `_ =` 静默吞掉）。</summary>
    private async Task<(SourceSession? Session, string? Error)> TryConnectAsync(ServerEntry entry, bool persist)
    {
        if (!ApiClient.IsSourcePortalUrl(entry.BaseUrl))
        {
            ClientLog.Write($"拒绝连接：{entry.BaseUrl} 不是源门户地址");
            return (null, "这条配置缺源地址（地址应形如 http://主机:端口/s/{源标识}）");
        }

        // 自动重连用短超时：连不上要赶紧给出结果，不能让人对着空白窗口等 20 秒
        var api = new ApiClient(entry.BaseUrl, entry.Token, TimeSpan.FromSeconds(4));
        try
        {
            var auth = await api.AuthStateAsync().ConfigureAwait(false);
            if (auth.AuthRequired && !auth.Authenticated)
            {
                api.Dispose();
                return (null, entry.Token is { Length: > 0 }
                    ? "令牌无效或已失效"
                    : "这个源要求令牌，请填「访问令牌」或重新扫码");
            }

            string? version = null;
            try { version = (await api.HealthAsync().ConfigureAwait(false)).Version; } catch { /* 非必需 */ }

            var sources = await api.SourcesAsync().ConfigureAwait(false);

            // 同一条纪律在会话层再守一次：门户只可能给一条源，多给就是"这不是门户"。
            if (sources.Count != 1)
            {
                api.Dispose();
                return (null, $"这个地址返回了 {sources.Count} 个源，不是源门户地址");
            }

            if (persist) ConnectionStore.Upsert(entry.BaseUrl, entry.Token, entry.Name);
            ClientLog.Write($"连接成功：{entry.BaseUrl}（源「{sources[0].Name}」，v{version}）");
            return (NewSession(entry, sources, api, version), null);
        }
        catch (Exception e) when (e is ApiException or NetworkException or InvalidOperationException)
        {
            ClientLog.Write($"连接失败：{entry.BaseUrl} → {e.Message}");
            api.Dispose();
            return (null, e.Message);
        }
        catch (Exception e)
        {
            ClientLog.Write($"连接异常：{entry.BaseUrl} → {e}");
            api.Dispose();
            return (null, e.Message);
        }
    }

    // ── 会话池 ─────────────────────────────────────────────────────

    /// <summary>同 base URL 的旧会话就地替换（重建了连接，不能留两个 ApiClient）。</summary>
    /// <param name="replacing">正在被编辑的那个会话。地址改过的话 baseUrl 对不上，得按对象移除。</param>
    private SourceSession AddSession(ServerEntry entry, List<SourceInfoDto> sources, ApiClient api, string? version,
        SourceSession? replacing = null)
    {
        var session = NewSession(entry, sources, api, version);
        AddSessionInternal(session, replacing);
        return session;
    }

    private SourceSession NewSession(ServerEntry entry, List<SourceInfoDto> sources, ApiClient api, string? version) =>
        new(entry, sources, api, version, Activate, RequestEditSource, RequestRemoveSource);

    private void AddSessionInternal(SourceSession session, SourceSession? replacing = null)
    {
        // 地址被改过的编辑：旧会话还在池子里但已无人指向，先拔掉（否则左栏会多一行僵尸源）
        if (replacing is not null && !ReferenceEquals(replacing, session) &&
            !string.Equals(replacing.Entry.BaseUrl, session.Entry.BaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            if (_sessions.Remove(replacing))
            {
                replacing.Api.Dispose();
                if (ReferenceEquals(_current, replacing))
                {
                    _current = null;
                    _currentPageSession = null;
                }

                ClientLog.Write($"编辑源：{replacing.Entry.BaseUrl} → {session.Entry.BaseUrl}");
            }
        }

        var dup = _sessions.FirstOrDefault(s =>
            string.Equals(s.Entry.BaseUrl, session.Entry.BaseUrl, StringComparison.OrdinalIgnoreCase));
        if (dup is not null)
        {
            var wasCurrent = ReferenceEquals(dup, _current);
            var index = _sessions.IndexOf(dup);
            _sessions[index] = session;
            dup.Api.Dispose();
            if (wasCurrent) _current = session;
        }
        else
        {
            _sessions.Add(session);
        }

        OnPropertyChanged(nameof(HasSessions));
        DisconnectCommand.RaiseCanExecuteChanged();
    }

    /// <summary>切到某个源。当前页整页重建（列表滚动位置不保留，与手机上换源一致）。</summary>
    public void Activate(SourceSession session) => OnUi(() =>
    {
        if (!_sessions.Contains(session)) return;

        foreach (var s in _sessions) s.IsCurrent = ReferenceEquals(s, session);
        _current = session;
        ServerLabel = session.Label;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsConnected));
        DisconnectCommand.RaiseCanExecuteChanged();

        if (CurrentPage is HomeViewModel && ReferenceEquals(_currentPageSession, session)) return;

        (CurrentPage as IDisposable)?.Dispose();
        _currentPageSession = session;

        var home = new HomeViewModel(session.Entry, session.Sources, session.ServerVersion, session.Api, this, session);
        CurrentPage = home;

        // 首屏数据必须在 UI 消息循环里发起：构造阶段直接 await 不保证回到 UI 线程
        var autoKey = _autoPlayConsumed ? null : Program.AutoPlayKey;
        _autoPlayConsumed = true;
        if (autoKey is null) Dispatcher.UIThread.Post(() => _ = home.StartAsync());
        else Dispatcher.UIThread.Post(() => _ = home.AutoPlayAsync(autoKey));
    });

    private SourceSession? _currentPageSession;

    /// <summary>从「改」进连接页时记下被编辑的会话，连上新连接后据此拔掉旧的。</summary>
    private SourceSession? _editingSession;

    public void RequestAddSource() => GoConnect(null);

    public void RequestEditSource(SourceSession session)
    {
        if (!_sessions.Contains(session)) return;
        GoConnect(null, isError: false, editing: session);
    }

    public void RequestRemoveSource(SourceSession session) => OnUi(() =>
    {
        if (!_sessions.Remove(session)) return;

        session.Api.Dispose();
        ClientLog.Write($"移除源：{session.Entry.BaseUrl}，剩余 {_sessions.Count} 个（配置保留）");
        OnPropertyChanged(nameof(HasSessions));
        DisconnectCommand.RaiseCanExecuteChanged();

        // 不是当前页：只从列表里消失，当前页不动
        if (!ReferenceEquals(_current, session)) return;

        _current = null;
        _currentPageSession = null;
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = null;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsConnected));
        ServerLabel = _sessions.Count > 0 ? "未选中源" : "未连接";

        var next = _sessions.FirstOrDefault();
        if (next is not null) Activate(next);
        else GoConnect($"「{session.Title}」已断开。配置还留着，连接页随时能重连。");
    });

    private Task DisconnectCurrentAsync()
    {
        var session = _current;
        if (session is null) return Task.CompletedTask;

        _sessions.Remove(session);
        session.Api.Dispose();
        _current = null;
        _currentPageSession = null;
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = null;
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsConnected));
        ServerLabel = _sessions.Count > 0 ? "未选中源" : "未连接";
        DisconnectCommand.RaiseCanExecuteChanged();
        ClientLog.Write($"断开源：{session.Entry.BaseUrl}，剩余 {_sessions.Count} 个");

        var next = _sessions.FirstOrDefault();
        if (next is not null) Activate(next);
        else GoConnect($"「{session.Title}」已断开。配置还留着，随时能重连。");

        return Task.CompletedTask;
    }
}
