using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>打开「我的」页（顶栏右侧那个人像）。</summary>
    void OpenMine();
}

/// <summary>
/// 窗口外壳，布局与 Android 一一对应：
///
/// <code>
/// ┌─────────────────────────────────────────────┐
/// │ ☰  MediaRack · 当前视图          🔍  👤     │  ← 顶栏
/// ├─────────────────────────────────────────────┤
/// │                                             │
/// │            海报墙（空源时是空态）            │  ← 内容区
/// │                                             │
/// └─────────────────────────────────────────────┘
/// </code>
///
/// <b>三块状态各自独立</b>：
/// <list type="bullet">
/// <item><b>内容区</b>：主页（海报墙）/ 「我的」/ 空态（一个源都没连上）。</item>
/// <item><b>左抽屉</b>：源列表 + 浏览 / 收藏夹导航，☰ 开合，默认收起——手机上一进来也只看到海报墙。</item>
/// <item><b>连接页</b>：不是页面而是覆盖层。加源 / 改源都从抽屉点出来，关掉就回到刚才的墙。</item>
/// </list>
///
/// 同时可以连多个源（与 Android 抽屉里那份源列表同一个模型）：每个源一个
/// <see cref="SourceSession"/>，自带 ApiClient；切源只换当前页，不断开别的源。
/// </summary>
public sealed class ShellViewModel : ViewModelBase, ISessionHost
{
    private readonly ObservableCollection<SourceSession> _sessions = new();

    private object? _currentPage;
    private HomeViewModel? _homePage;
    private SourceSession? _current;
    private SourceSession? _currentPageSession;
    private SourceSession? _editingSession;
    private ConnectViewModel? _connect;

    private bool _isDrawerOpen;
    private bool _autoPlayConsumed;
    private string _title = "MediaRack";
    private string _emptyTitle = "还没有任何源";
    private string _emptyText = "点左上角 ☰ 打开源列表，再点「＋ 添加源」：填服务器地址、源地址和访问令牌。";

    public ShellViewModel()
        : this(savedOverride: null)
    {
    }

    /// <param name="savedOverride">
    /// 非空 = 直接用这份配置启动，不读（也不写）磁盘上的 <c>client-servers.json</c>。
    /// 无头自检（<c>--shelltest</c>）要能确定地摆出"一个源都没有"的状态，不能受本机
    /// 已有配置影响——否则同一个用例在开发机和别人的机器上结论不同。
    /// </param>
    internal ShellViewModel(List<ServerEntry>? savedOverride)
    {
        ToggleDrawerCommand = new RelayCommand(() => IsDrawerOpen = !IsDrawerOpen);
        CloseDrawerCommand = new RelayCommand(() => IsDrawerOpen = false);
        AddSourceCommand = new RelayCommand(RequestAddSource);
        OpenMineCommand = new RelayCommand(OpenMine);
        CloseMineCommand = new RelayCommand(CloseMine);
        CloseConnectCommand = new RelayCommand(CloseConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectCurrentAsync, () => _current is not null);
        SwitchSessionCommand = new RelayCommand<SourceSession>(Activate);

        if (savedOverride is not null)
        {
            ClientLog.Write($"自检模式：用给定的 {savedOverride.Count} 条配置，不读磁盘");
            if (savedOverride.Count > 0) Dispatcher.UIThread.Post(() => _ = ResumeAllAsync(savedOverride));
            return;
        }

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

        // 一条都没有就停在空海报墙上，不弹连接页：手机上第一次装完也是空的墙 + 抽屉。
        if (saved.Count == 0) return;
        Dispatcher.UIThread.Post(() => _ = ResumeAllAsync(saved));
    }

    // ── 会话池 ─────────────────────────────────────────────────────

    public ObservableCollection<SourceSession> Sessions => _sessions;

    public RelayCommand ToggleDrawerCommand { get; }
    public RelayCommand CloseDrawerCommand { get; }
    public RelayCommand AddSourceCommand { get; }
    public RelayCommand OpenMineCommand { get; }
    public RelayCommand CloseMineCommand { get; }
    public RelayCommand CloseConnectCommand { get; }
    public RelayCommand<SourceSession> SwitchSessionCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }

    public bool HasSessions => _sessions.Count > 0;

    // ── 内容区 ─────────────────────────────────────────────────────

    /// <summary>当前页：<see cref="HomeViewModel"/> / <see cref="MineViewModel"/> / null（空态）。</summary>
    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!Set(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(Home));
            OnPropertyChanged(nameof(Mine));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsMine));
            OnPropertyChanged(nameof(IsConnected));
            DisconnectCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>抽屉里那份导航绑它（抽屉长在壳上，数据还在主页里——两边都只有一份）。</summary>
    public HomeViewModel? Home => _currentPage as HomeViewModel;

    public MineViewModel? Mine => _currentPage as MineViewModel;

    /// <summary>一个源都没连上：内容区画空海报墙 + 一句"去添加源"。</summary>
    public bool IsEmpty => _currentPage is null;

    public bool IsMine => _currentPage is MineViewModel;

    public bool IsConnected => _current is not null;

    public SourceSession? Current => _current;

    /// <summary>顶栏中间那行字：当前视图名（切导航 / 进合集内部都会变）。</summary>
    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string EmptyTitle
    {
        get => _emptyTitle;
        private set => Set(ref _emptyTitle, value);
    }

    public string EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    // ── 左抽屉 ─────────────────────────────────────────────────────

    /// <summary>抽屉开着没。默认收起——和手机一致：一进来就是海报墙，要看源列表点 ☰。</summary>
    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set => Set(ref _isDrawerOpen, value);
    }

    // ── 连接页（覆盖层，不是页面）──────────────────────────────────

    public ConnectViewModel? Connect
    {
        get => _connect;
        private set
        {
            if (!Set(ref _connect, value)) return;
            OnPropertyChanged(nameof(IsConnectOpen));
        }
    }

    public bool IsConnectOpen => _connect is not null;

    // ── 线程纪律 ───────────────────────────────────────────────────

    /// <summary>UI 状态只能在 UI 线程改；自动重连的回调可能来自后台线程。</summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ── 连接页 ─────────────────────────────────────────────────────

    /// <summary>打开连接页。已连着的源<b>不释放</b>——关掉覆盖层就回到原来的墙。</summary>
    /// <param name="editing">非空 = 从某个源点「改」进来的，四栏预填成它的值。</param>
    private void ShowConnect(string? notice = null, bool isError = false, SourceSession? editing = null) => OnUi(() =>
    {
        IsDrawerOpen = false;
        _editingSession = editing;
        Connect = new ConnectViewModel(OnConnected, notice, isError,
            // 已连着的配置在列表里标「已连接」，不给重复连
            baseUrl => _sessions.Any(s =>
                string.Equals(s.Entry.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)),
            editing?.Entry,
            onCancel: CloseConnect);
    });

    private void CloseConnect() => OnUi(() =>
    {
        Connect = null;
        _editingSession = null;
    });

    // ── 连接 ───────────────────────────────────────────────────────

    /// <summary>连接页点「连接」成功后回来：关覆盖层、入池、立刻切过去。</summary>
    private void OnConnected(ServerEntry entry, List<SourceInfoDto> sources, ApiClient api, string? version) =>
        OnUi(() =>
        {
            // 从「改」进来的：地址可能被改过，旧会话按 baseUrl 认不出来，得主动拔掉
            var replacing = _editingSession;
            Connect = null;
            _editingSession = null;
            Activate(AddSession(entry, sources, api, version, replacing));
        });

    /// <summary>启动时并发连全部已保存的源——快的那台先上屏，慢的不拖住启动。</summary>
    private async Task ResumeAllAsync(List<ServerEntry> saved)
    {
        ClientLog.Write($"启动并发连接 {saved.Count} 个源");
        OnUi(() =>
        {
            EmptyTitle = "正在连接…";
            EmptyText = $"共 {saved.Count} 个源";
        });

        var tasks = saved.Select(e => TryConnectAsync(e, persist: true)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(true);

        var ok = results.Where(r => r.Session is not null).Select(r => r.Session!).ToList();
        ClientLog.Write($"启动连接结果：成功 {ok.Count} / {saved.Count}");

        if (ok.Count == 0)
        {
            // 一个都没连上：停在空墙 + 说清原因，不弹连接页（弹了就盖住了这句话）
            var why = results.Select(r => r.Error).FirstOrDefault(m => m is { Length: > 0 });
            OnUi(() =>
            {
                EmptyTitle = "已保存的源都连不上";
                EmptyText = why is { Length: > 0 }
                    ? $"{why}。检查服务端是否开着，或从 ☰ 里改一下地址。"
                    : "检查服务端是否开着，或从 ☰ 里改一下地址。";
            });
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
        else ShowConnect($"{entry.DisplayName} 连不上（{r.Error}）", isError: true);
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

    // ── 会话池维护 ─────────────────────────────────────────────────

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

    /// <summary>
    /// 自检入口：把一个"已经连好"的会话塞进池子。<c>--shelltest</c> 不联网，
    /// 但仍要能验证"切源 → 我的 → 返回 → 移除"这条链路。
    /// </summary>
    internal SourceSession AttachForTest(ServerEntry entry, List<SourceInfoDto> sources, ApiClient api, string? version)
    {
        var session = NewSession(entry, sources, api, version);
        AddSessionInternal(session);
        return session;
    }

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
                    _homePage = null;
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
        IsDrawerOpen = false;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsConnected));
        DisconnectCommand.RaiseCanExecuteChanged();

        if (CurrentPage is HomeViewModel && ReferenceEquals(_currentPageSession, session)) return;

        if (_homePage is not null) _homePage.PropertyChanged -= OnHomePropertyChanged;

        _currentPageSession = session;
        var home = new HomeViewModel(session.Entry, session.Sources, session.ServerVersion, session.Api, this, session);
        home.PropertyChanged += OnHomePropertyChanged;
        _homePage = home;
        CurrentPage = home;
        Title = home.HeadingText;

        // 首屏数据必须在 UI 消息循环里发起：构造阶段直接 await 不保证回到 UI 线程
        var autoKey = _autoPlayConsumed ? null : Program.AutoPlayKey;
        _autoPlayConsumed = true;
        if (autoKey is null) Dispatcher.UIThread.Post(() => _ = home.StartAsync());
        else Dispatcher.UIThread.Post(() => _ = home.AutoPlayAsync(autoKey));
    });

    /// <summary>顶栏标题跟着当前视图走（"全部视频" → "继续观看" → 「某个合集」）。</summary>
    private void OnHomePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.HeadingText) && sender is HomeViewModel home)
            Title = home.HeadingText;
    }

    // ── 我的页 ─────────────────────────────────────────────────────

    public void OpenMine() => OnUi(() =>
    {
        IsDrawerOpen = false;
        if (_currentPage is MineViewModel) return;

        var mine = new MineViewModel(_current, this);
        CurrentPage = mine;
        Title = "我的";
        Dispatcher.UIThread.Post(() => _ = mine.LoadAsync());
    });

    /// <summary>「我的」左上角返回：回到刚才那张墙（同一个 HomeViewModel，列表位置都还在）。</summary>
    public void CloseMine() => OnUi(() =>
    {
        if (_currentPage is not MineViewModel) return;
        (_currentPage as IDisposable)?.Dispose();

        if (_homePage is not null && ReferenceEquals(_homePage.Session, _current))
        {
            CurrentPage = _homePage;
            Title = _homePage.HeadingText;
            return;
        }

        CurrentPage = null;
        Title = "MediaRack";
    });

    // ── 添加 / 编辑 / 移除 ─────────────────────────────────────────

    public void RequestAddSource() => ShowConnect();

    public void RequestEditSource(SourceSession session)
    {
        if (!_sessions.Contains(session)) return;
        ShowConnect(editing: session);
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
        if (_homePage is not null) _homePage.PropertyChanged -= OnHomePropertyChanged;
        _homePage = null;
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = null;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsConnected));
        Title = "MediaRack";

        var next = _sessions.FirstOrDefault();
        if (next is not null) Activate(next);
    });

    private Task DisconnectCurrentAsync()
    {
        var session = _current;
        if (session is null) return Task.CompletedTask;
        RequestRemoveSource(session);
        return Task.CompletedTask;
    }
}
