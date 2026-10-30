using System.Collections.ObjectModel;
using Avalonia.Threading;
using MediaRack.Client.Media;
using MediaRack.Client.Remote;
using MediaRack.Client.Storage;
using MediaRack.Client.Tools;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 连上服务端之后的主界面：左侧导航（全部 / 继续观看 / 合集 / 各源 / 收藏夹）+ 工具栏 + 内容区。
///
/// <b>内容区有两种形态</b>：海报墙（默认）与合集网格（导航选中「合集」且没点进某个合集时）。
/// 两者共用同一个加载壳（<see cref="ReloadAsync"/>），所以错误条、载入态、去重都只有一份。
///
/// 三条数据口径要分清，别混：
/// - <b>全部视频</b> 走 <c>/api/v1/library</c>（聚合，同名 key 跨源去重）
/// - <b>单源</b> 走 <c>/api/v1/sources/{id}/library</c>（不去重，行数即该源条目数）
/// - <b>收藏夹</b> 走聚合并带 <c>favorite=</c>，排序 <c>sort=added</c>
/// - <b>继续观看</b> 走聚合并带 <c>sort=recent</c>（服务端按 <c>last_play_at</c> 倒序，
///   且只留"位置 &gt; 0"的条目 —— 只是点开过的不算看过）
///
/// 分页是"续读"式：页大小 60，滚到底自动追加；搜索 / 切源 / 换合集都会重头开始。
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 60;

    private readonly ApiClient _api;
    private readonly ISessionHost? _host;
    private readonly List<MediaCard> _cards = new();
    private DispatcherTimer? _searchDebounce;

    private NavItem? _nav;

    /// <summary>导航选中「合集」时，当前点进去的那个合集（null = 还停在合集网格上）。</summary>
    private CollectionCard? _openCollection;

    private CollectionOption? _collection;
    private MediaCard? _selectedCard;
    private PlayerViewModel? _player;
    private string _query = "";
    private string _error = "";
    private string _hint = "";
    private int _columns = 4;
    private int _total;
    private bool _isLoading;
    private bool _canLoadMore;
    private bool _sortAdded;
    private bool _syncingNav;
    private bool _rebuildingCollections;
    private bool _pendingReload;
    private string _loadedSignature = "";
    private DateTime _lastLoadStarted;
    private bool _disposed;

    public HomeViewModel(ServerEntry server, List<SourceInfoDto> sources, string? serverVersion, ApiClient api,
        ISessionHost? host = null, SourceSession? session = null)
    {
        _api = api;
        _host = host;
        Server = server;
        Session = session;
        ServerLabel = session?.Label ?? server.DisplayName + (serverVersion is { Length: > 0 } v ? $" · v{v}" : "");

        SourceNavs = new ObservableCollection<NavItem>
        {
            new("all", null, "全部视频", $"{sources.Sum(s => s.ItemCount):N0} 条"),
            new("recent", null, "继续观看", "按上次播放排序"),
            new("collections", null, "合集", "按 UP 主 / 目录"),
        };
        // 一条配置 = 一个源（门户地址），所以这里恒为一条：「全部视频」就是它，
        // 再单独列一行等于把同一份列表画两遍。

        FavoriteNavs = new ObservableCollection<NavItem>();
        Collections = new ObservableCollection<CollectionOption> { new(null, "全部合集", 0) };

        SelectCommand = new RelayCommand<NavItem>(OnNavPicked);
        LoadMoreCommand = new AsyncRelayCommand(() => LoadPageAsync(), () => CanLoadMore && !IsLoading);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CloseDetailCommand = new RelayCommand<MediaCard>(_ => SelectedCard = null);
        PlayCommand = new RelayCommand<MediaCard>(StartPlay);
        SwitchSessionCommand = new RelayCommand<SourceSession>(s => _host?.Activate(s));
        AddSourceCommand = new RelayCommand(() => _host?.RequestAddSource());

        InitCollections();
        InitFavorites();

        // 搜索防抖：每敲一个字就发一次请求会把服务端 SQL 打成筛子，
        // 350ms 足够覆盖正常打字节奏，停了才查。
        // 计时器延迟到第一次输入才建：无 UI 的自动化测试（--vmtest）里没有消息循环，
        // 构造时就建会白拿一个永不 Tick 的定时器。
        _nav = SourceNavs[0];
    }

    public ServerEntry Server { get; }
    public string ServerLabel { get; }

    /// <summary>这一页属于哪个源（会话池里的那一条）。</summary>
    public SourceSession? Session { get; }

    /// <summary>左栏「源」那段：这台机器上所有连着的源，点一行换一页。</summary>
    public ObservableCollection<SourceSession> Sessions => _host?.Sessions ?? _emptySessions;

    private static readonly ObservableCollection<SourceSession> _emptySessions = new();

    public bool HasSessions => _host is not null;

    public RelayCommand<SourceSession> SwitchSessionCommand { get; }
    public RelayCommand AddSourceCommand { get; }

    public ObservableCollection<NavItem> SourceNavs { get; }
    public ObservableCollection<NavItem> FavoriteNavs { get; }
    public ObservableCollection<CollectionOption> Collections { get; }
    public ObservableCollection<MediaRow> Rows { get; } = new();

    /// <summary>给视图的图片控件用（Loaded 时按卡片取图）。</summary>
    public PosterCache Posters { get; } = new();

    public RelayCommand<NavItem> SelectCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand<MediaCard> CloseDetailCommand { get; }
    public RelayCommand<MediaCard> PlayCommand { get; }

    // ── 播放 ───────────────────────────────────────────────────────

    /// <summary>取（必要时创建）那个唯一的播放器实例。第一次点播放才会初始化 libvlc。</summary>
    private PlayerViewModel EnsurePlayer()
    {
        if (_player is not null) return _player;

        _player = new PlayerViewModel(_api);
        _player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.IsOpen)) OnPropertyChanged(nameof(HasPlayer));
        };
        _player.Closed += OnPlayerClosed;
        OnPropertyChanged(nameof(Player));
        return _player;
    }

    private void StartPlay(MediaCard card)
    {
        if (card is null) return;

        // 把"当前看得见的这份列表"整份带进播放页（APK 的 PlayQueue 同款语义）：
        // 上一个/下一个、播完自动切集、连播计数都靠它，退回来还是这一屏。
        var items = _cards
            .Select(c => new PlayQueueItem(c.SourceId, c.Key, c.Title, CardSubtitle(c)))
            .ToList();

        var index = _cards.FindIndex(c => c.Key == card.Key && c.SourceId == card.SourceId);
        if (index < 0)
        {
            // 卡片来自"详情侧栏残留"这类边角情况：队列是空的就单条播，别把位置算错
            items.Insert(0, new PlayQueueItem(card.SourceId, card.Key, card.Title, CardSubtitle(card)));
            index = 0;
        }

        _ = EnsurePlayer().PlayAsync(items, index, QueueLabel());
        OnPropertyChanged(nameof(HasPlayer));
    }

    private static string CardSubtitle(MediaCard card) =>
        card.CollectionName.Length > 0 ? $"{card.SourceId} · {card.CollectionName}" : card.SourceId;

    /// <summary>连播标签：`连播：稍后再看` / `连播：BILIBILI · 某UP` / `连播：搜索“xxx”`。</summary>
    private string QueueLabel()
    {
        var where = _nav switch
        {
            null => "全部",
            { IsFavorite: true } n => n.Title,
            _ => _collection is { Id: not null } col ? $"{_nav.Title} · {col.Label}" : _nav.Title,
        };

        if (_query.Length > 0) return $"连播：搜索“{_query}”";
        return $"连播：{where}";
    }

    /// <summary>关闭播放页：只就地更新那一张卡片的续播条，不整表重载（重载会把滚动位置弹回顶部）。</summary>
    private void OnPlayerClosed(object? sender, PlayClosedEventArgs e)
    {
        var card = _cards.FirstOrDefault(c => c.Key == e.Key && c.SourceId == e.SourceId);
        card?.ApplyProgress(e.PositionMs, e.DurationMs, e.Completed);
        OnPropertyChanged(nameof(HasPlayer));
        ClientLog.Write($"播放页关闭：{e.SourceId}/{e.Key} @ {e.PositionMs}ms" +
                        $"（时长 {e.DurationMs}ms，completed={e.Completed}）" +
                        (card is null ? "，列表里没有这张卡片" : $"，卡片续播条={card.ProgressText ?? "无"}"));
    }

    // ── 侧栏选中（两个 ListBox 互斥）──────────────────────────────

    public NavItem? SelectedSourceNav
    {
        get => _nav is { IsFavorite: false } ? _nav : null;
        set { if (!_syncingNav && value is not null) ApplyNav(value); }
    }

    public NavItem? SelectedFavoriteNav
    {
        get => _nav is { IsFavorite: true } ? _nav : null;
        set { if (!_syncingNav && value is not null) ApplyNav(value); }
    }

    private void OnNavPicked(NavItem item) => ApplyNav(item);

    private void ApplyNav(NavItem item)
    {
        if (!ReferenceEquals(_nav, item))
        {
            _nav = item;
            // 换导航一定退出"某个合集内部"：不回网格就直接换到别的源，回来时状态说不清
            _openCollection = null;
            // 合集跟着源走：换源后原来的合集 id 可能不属于这个源，回到「全部合集」
            _collection = Collections.Count > 0 ? Collections[0] : null;
            NotifyLayoutChanged();
            _ = ReloadAsync(alsoReloadNavLists: true);
        }

        _syncingNav = true;
        try
        {
            OnPropertyChanged(nameof(SelectedSourceNav));
            OnPropertyChanged(nameof(SelectedFavoriteNav));
        }
        finally
        {
            _syncingNav = false;
        }
    }

    /// <summary>导航 / 内容区形态变化后，一次性把所有布局相关属性通知出去。</summary>
    private void NotifyLayoutChanged()
    {
        OnPropertyChanged(nameof(SelectedCollection));
        OnPropertyChanged(nameof(ShowSort));
        OnPropertyChanged(nameof(ShowCollectionGrid));
        OnPropertyChanged(nameof(ShowCollectionFilter));
        OnPropertyChanged(nameof(HeadingText));
        OnPropertyChanged(nameof(ShowCollectionBack));
        OnPropertyChanged(nameof(ShowFavoriteTools));
        OnPropertyChanged(nameof(IsInFavorite));
    }

    // ── 工具栏 ─────────────────────────────────────────────────────

    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value)) return;
            EnsureDebounce().Stop();
            _searchDebounce!.Start();
        }
    }

    private DispatcherTimer EnsureDebounce()
    {
        if (_searchDebounce is not null) return _searchDebounce;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce?.Stop();
            _ = ReloadAsync();
        };
        return _searchDebounce;
    }

    /// <summary>程序化搜索（自检 / 将来的快捷键）：跳过防抖，立刻查。</summary>
    public Task SearchNowAsync(string query)
    {
        _query = query;
        OnPropertyChanged(nameof(Query));
        return ReloadAsync();
    }

    public CollectionOption? SelectedCollection
    {
        get => _collection;
        set
        {
            // 依次挡掉三种"不是用户在选"的情况：
            // 1) null —— 下拉列表重建时 ComboBox 会先把 SelectedItem 清成 null 回推一次
            // 2) 重建期间 —— 列表刚填完，ComboBox 自动选中第 0 项
            // 3) 同一个合集换了对象 —— 列表重建后新实例和旧实例 Id 相同，
            //    引用比较会当成"用户换了合集"，白加载一次（表现为切一次源请求两遍）
            if (value is null || _rebuildingCollections) return;

            if (_collection is not null && _collection.Id == value.Id)
            {
                _collection = value;
                return;
            }

            if (!Set(ref _collection, value)) return;
            _ = ReloadAsync();
        }
    }

    /// <summary>sort=added 只在收藏夹里有意义（加入时间在 favorite_item 表里）。</summary>
    public bool ShowSort => _nav is { IsFavorite: true };

    /// <summary>合集下拉只对"能跨目录看"的视图有意义；收藏夹/继续观看/合集内部都不用它。</summary>
    public bool ShowCollectionFilter => _nav is { Kind: "all" or "source" };

    // ── 合集页（导航「合集」的两种状态：网格 / 某个合集内部）─────────────
    //
    // 合集是只读结构层（源级、平铺、排他），所以这一页没有编辑动作，
    // 只有"点进去看"和"退回网格"。共 955 个合集，所以网格自带过滤框。

    /// <summary>当前停在合集网格上（选中「合集」且没点进具体某个）。</summary>
    public bool ShowCollectionGrid => _nav is { IsCollections: true } && _openCollection is null;

    /// <summary>正在看某个合集内部（工具栏出现"← 合集"）。</summary>
    public bool ShowCollectionBack => _nav is { IsCollections: true } && _openCollection is not null;

    /// <summary>顶部标题行：合集内部要显示自己是谁，否则用户不知道点进了哪。</summary>
    public string HeadingText => _openCollection is { } c
        ? $"{c.Name} · {c.ItemCount:N0} 条"
        : _nav?.Title ?? "全部视频";

    public void OpenCollection(CollectionCard card)
    {
        if (card is null) return;
        _openCollection = card;
        _query = "";
        OnPropertyChanged(nameof(Query));
        NotifyLayoutChanged();
        _ = ReloadAsync();
    }

    // ── 从「我的」页跳进来 ─────────────────────────────────────────
    //
    // 「我的」只负责入口，真正的列表还是首页这一份（收藏夹的增删改、合集网格的过滤框都在这里），
    // 所以在那边点一下等于在这里点一下同名导航，不另起一套数据。

    /// <summary>收藏的视频：切到第一个收藏夹（没有收藏夹就停在原地）。</summary>
    public void OpenFavoritesView()
    {
        var target = FavoriteNavs.FirstOrDefault(n => n.IsFavorite);
        if (target is not null) ApplyNav(target);
    }

    /// <summary>收藏的合集 / 合集网格：切到导航里的「合集」。</summary>
    public void OpenCollectionsView()
    {
        var target = SourceNavs.FirstOrDefault(n => n.IsCollections);
        if (target is not null) ApplyNav(target);
    }

    /// <summary>继续观看。</summary>
    public void OpenRecentView()
    {
        var target = SourceNavs.FirstOrDefault(n => n.IsRecent);
        if (target is not null) ApplyNav(target);
    }

    public void BackToCollectionGrid()
    {
        if (_openCollection is null) return;
        _openCollection = null;
        _query = "";
        OnPropertyChanged(nameof(Query));
        NotifyLayoutChanged();
        _ = ReloadAsync();
    }

    public bool SortAdded
    {
        get => _sortAdded;
        set
        {
            if (!Set(ref _sortAdded, value)) return;
            _ = ReloadAsync();
        }
    }

    // ── 状态 ───────────────────────────────────────────────────────

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value)) LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanLoadMore
    {
        get => _canLoadMore;
        private set
        {
            if (Set(ref _canLoadMore, value)) LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }

    public int Total
    {
        get => _total;
        private set
        {
            if (Set(ref _total, value)) OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => IsLoading && _cards.Count == 0
        ? "载入中…"
        : $"{_cards.Count:N0} / {Total:N0} 条";

    /// <summary>已加载卡片数（自检 / 诊断用；界面看 CountText）。</summary>
    public int LoadedCount => _cards.Count;

    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    public string Hint
    {
        get => _hint;
        set
        {
            if (Set(ref _hint, value)) OnPropertyChanged(nameof(HasHint));
        }
    }

    public bool HasHint => _hint.Length > 0;

    public string EmptyText
    {
        get
        {
            if (IsLoading || Rows.Count > 0 || HasError) return "";
            if (_query.Trim().Length > 0) return $"没有匹配「{_query.Trim()}」的条目";

            return _nav switch
            {
                { IsRecent: true } => "还没有播过任何视频（播过的会按上次播放时间排在这里）",
                { IsFavorite: true } => "这个收藏夹还是空的（在详情侧栏点收藏夹名字就能加进来）",
                _ => "这里没有条目",
            };
        }
    }

    public MediaCard? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (!Set(ref _selectedCard, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            // 侧栏一打开就要显示"这条在哪些收藏夹里"，所以选中即拉一次归属
            ReloadFavoriteChips(value);
        }
    }

    public bool HasSelection => _selectedCard is not null;

    // ── 播放页（叠在海报墙上，返回后滚动位置原样保留）────────────────

    /// <summary>
    /// 全局只有一个播放器实例：换条目只换 Media，<c>VideoView</c> 的绑定不动、切条不黑屏。
    /// 惰性创建——只浏览不播放的用户不必为 libvlc 那 300 MB 付出加载时间。
    /// </summary>
    public PlayerViewModel? Player
    {
        get => _player;
        private set
        {
            // 必须发通知：视图用 ContentControl.Content 绑这个值，自动属性赋值视图是看不到的
            if (Set(ref _player, value)) OnPropertyChanged(nameof(HasPlayer));
        }
    }

    public bool HasPlayer => _player is { IsOpen: true };

    // ── 数据加载 ───────────────────────────────────────────────────

    /// <summary>首次进入页面时调用（构造里不能直接 await，必须由视图在 UI 线程触发）。</summary>
    public Task StartAsync() => ReloadAsync(alsoReloadNavLists: true);

    /// <summary>
    /// 启动即播（<c>--autoplay [key]</c>）：验收和排障时不必每次都手点一路。
    /// key 为空 = 播首屏第一条；<c>源/键</c> 形式可以播列表里没有的条目。
    /// </summary>
    public async Task AutoPlayAsync(string? key)
    {
        await ReloadAsync(alsoReloadNavLists: true);

        if (key is not { Length: > 0 })
        {
            var first = _cards.FirstOrDefault();
            if (first is not null) StartPlay(first);
            else ClientLog.Write("autoplay：首屏为空，跳过");
            return;
        }

        var card = _cards.FirstOrDefault(c => c.Key == key);
        if (card is not null)
        {
            StartPlay(card);
            return;
        }

        var slash = key.IndexOf('/');
        if (slash > 0)
        {
            var source = key[..slash];
            var mediaKey = key[(slash + 1)..];
            _ = EnsurePlayer().OpenAsync(source, mediaKey, mediaKey, source);
            OnPropertyChanged(nameof(HasPlayer));
            return;
        }

        ClientLog.Write($"autoplay：列表里找不到 {key}，跳过");
    }

    public async Task ReloadAsync(bool alsoReloadNavLists = false, [System.Runtime.CompilerServices.CallerMemberName] string reason = "?")
    {
        if (_disposed) return;

        if (IsLoading)
        {
            // 加载中又改了搜索词 / 切了源：记一笔，等这次回来再跑一遍，
            // 否则用户会觉得"输入没反应"
            _pendingReload = true;
            return;
        }

        // 同条件去重：ListBox / ComboBox 在重建列表时会把选中项再回推一次，
        // 那种重入的四元组（源 / 词 / 合集 / 排序）和刚查过的完全一样，
        // 再查一遍纯属浪费——现象是"点一下，服务端收到两次请求"。
        // 刷新按钮走 alsoReloadNavLists=true，绕过这里。
        var signature = $"{_nav?.Kind}:{_nav?.Id}|{_query.Trim()}|{_collection?.Id ?? ""}|" +
                        $"{_sortAdded}|{_openCollection?.Id ?? ""}";
        if (!alsoReloadNavLists &&
            signature == _loadedSignature &&
            (DateTime.UtcNow - _lastLoadStarted).TotalMilliseconds < 1500)
        {
            ClientLog.Write($"跳过重复加载（{reason}，条件未变）");
            _pendingReload = false;
            return;
        }

        _loadedSignature = signature;
        _lastLoadStarted = DateTime.UtcNow;

        IsLoading = true;
        Error = "";
        Hint = "";
        _searchDebounce?.Stop();

        try
        {
            if (alsoReloadNavLists) await ReloadNavListsAsync();

            _cards.Clear();
            Rows.Clear();
            Total = 0;
            CanLoadMore = false;
            SelectedCard = null;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(EmptyText));

            if (ShowCollectionGrid) await LoadCollectionGridAsync();
            else await LoadPageAsync();
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Error = e.Message;
        }
        catch (Exception e)
        {
            Error = "加载失败：" + e.Message;
            ClientLog.Write("列表加载异常：" + e);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(EmptyText));
        }

        if (_pendingReload && !_disposed)
        {
            _pendingReload = false;
            await ReloadAsync();
        }
    }

    /// <summary>收藏夹与合集是按源收窄的（合集尤其：602 个 UP 合集挂在 BILIBILI 名下）。</summary>
    private async Task ReloadNavListsAsync()
    {
        try
        {
            var favs = await _api.FavoritesAsync();

            // 列表重建后对象全是新的，而 _nav 还指着旧的 —— 不换掉的话 ListBox 匹配不到，
            // 用户在收藏夹里点一下"收藏"就会眼看侧栏取消高亮（其实还停在这一页）。
            var keep = _nav is { IsFavorite: true } ? _nav.Id : null;

            _syncingNav = true;
            try
            {
                FavoriteNavs.Clear();
                foreach (var f in favs)
                {
                    FavoriteNavs.Add(new NavItem("favorite", f.Id, f.Name, $"{f.ItemCount:N0} 条"));
                }

                if (keep is not null)
                {
                    var again = FavoriteNavs.FirstOrDefault(n => n.Id == keep);
                    if (again is not null) _nav = again;
                }

                OnPropertyChanged(nameof(SelectedSourceNav));
                OnPropertyChanged(nameof(SelectedFavoriteNav));
            }
            finally
            {
                _syncingNav = false;
            }
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            ClientLog.Write($"收藏夹列表加载失败：{e.Message}");
        }

        try
        {
            var sourceId = _nav is { Kind: "source" } ? _nav.Id : null;
            var collections = await _api.CollectionsAsync(sourceId);
            var keep = _collection?.Id;

            _rebuildingCollections = true;
            try
            {
                Collections.Clear();
                Collections.Add(new CollectionOption(null, "全部合集", collections.Count));
                foreach (var c in collections.OrderByDescending(c => c.ItemCount))
                {
                    Collections.Add(new CollectionOption(c.Id, c.Name, c.ItemCount));
                }

                _collection = Collections.FirstOrDefault(x => x.Id == keep) ?? Collections[0];
                OnPropertyChanged(nameof(SelectedCollection));
            }
            finally
            {
                _rebuildingCollections = false;
            }
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            ClientLog.Write($"合集列表加载失败：{e.Message}");
        }
    }

    private async Task<MediaPageDto> FetchAsync(int offset)
    {
        var q = _query.Trim();
        var nav = _nav ?? SourceNavs[0];

        // 合集内部：走单源列表 + collection 过滤。合集是源级的东西，跨源没有统一口径，
        // 所以不能用聚合端点（那会按 key 去重，把"这个合集里到底有什么"算错）。
        if (nav.IsCollections)
        {
            if (_openCollection is null) return new MediaPageDto { Total = 0 };
            return await _api.SourceLibraryAsync(_openCollection.SourceId, offset, PageSize,
                q, _openCollection.Id, null, null);
        }

        var collectionId = _collection?.Id;
        // three sorts, one field: 收藏夹看加入时间、继续观看看播放时间、其余按标题
        var sort = nav.IsFavorite ? (_sortAdded ? "added" : null)
            : nav.IsRecent ? "recent"
            : null;

        return nav.Kind switch
        {
            "source" => await _api.SourceLibraryAsync(nav.Id!, offset, PageSize, q, collectionId, null, sort),
            "favorite" => await _api.LibraryAsync(offset, PageSize, q, collectionId, nav.Id, sort),
            _ => await _api.LibraryAsync(offset, PageSize, q, collectionId, null, sort),
        };
    }

    private async Task LoadPageAsync()
    {
        CanLoadMore = false;

        MediaPageDto page;
        try
        {
            page = await FetchAsync(_cards.Count);
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            // 追加失败不该把已经加载的卡片清掉——只提示，用户还能点「重试」
            Error = e.Message;
            CanLoadMore = _cards.Count < Total;
            return;
        }

        Total = page.Total;

        // 继续观看视图：播过一点就画续播条，否则那一页看起来像"随便挑的一堆"
        var alwaysShowProgress = _nav is { IsRecent: true };
        foreach (var dto in page.Items)
        {
            _cards.Add(new MediaCard(dto, _api, OpenDetail, alwaysShowProgress));
        }

        AppendRows();

        // NextOffset 是服务端给的游标：比 "已加载 < total" 更可靠
        // （库在两次请求之间被扫描改过时，靠计数会多请求一页空数据）。
        CanLoadMore = page.NextOffset is not null && _cards.Count < Total;

        var sortLabel = _nav is { IsRecent: true } ? "recent" : _sortAdded ? "added" : "-";
        var collectionLabel = _openCollection is { } opened
            ? opened.Name
            : string.IsNullOrEmpty(_collection?.Id) ? "全部" : _collection!.Label;

        ClientLog.Write(
            $"列表 {HeadingText} 合集={collectionLabel} " +
            $"q='{_query.Trim()}' sort={sortLabel} → " +
            $"{_cards.Count}/{Total}，{Rows.Count} 行 × {_columns} 列");

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void OpenDetail(MediaCard card) => SelectedCard = card;

    // ── 行切分（虚拟化的前提）──────────────────────────────────────

    /// <summary>
    /// 按当前列数把卡片切成行。追加时只补新行；上一页末尾那个半行先撤掉再重排，
    /// 否则新一页的第一张会接在半行后面，右边界对不齐。
    /// </summary>
    private void AppendRows()
    {
        if (_columns <= 0) return;

        while (Rows.Count > 0 && Rows[^1].Cards.Count < _columns) Rows.RemoveAt(Rows.Count - 1);

        var start = Rows.Count * _columns;
        for (var i = start; i < _cards.Count; i += _columns)
        {
            Rows.Add(new MediaRow(_cards.Skip(i).Take(_columns).ToList(), _columns));
        }
    }

    /// <summary>视图宽度变化时调用。列数变了必须整表重排（行对象的列数是构造参数）。</summary>
    public void SetColumns(int columns)
    {
        columns = Math.Clamp(columns, 1, 10);
        if (columns == _columns) return;
        _columns = columns;
        Rows.Clear();
        AppendRows();
    }

    /// <summary>视图滚到接近底部时调用（连续加载，省掉用户点「加载更多」）。</summary>
    public void MaybeLoadMore()
    {
        if (!CanLoadMore || IsLoading) return;
        _ = LoadMoreCommand.ExecuteAsync();
    }

    private async Task RefreshAsync() => await ReloadAsync(alsoReloadNavLists: true);

    public void Dispose()
    {
        _disposed = true;
        _searchDebounce?.Stop();
        Player?.Dispose();
        Posters.Dispose();
    }
}
