using System.Collections.ObjectModel;
using Avalonia.Threading;
using MediaRack.Client.Media;
using MediaRack.Client.Remote;
using MediaRack.Client.Tools;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 「我的」页——与 Android 的 <c>MineScreen</c> 一一对应：头部、四宫格统计、继续观看、收藏的两个入口。
///
/// <para>
/// 桌面端没有下载 / 本地缓存，所以「已下载」和「占用」恒为 0。仍然照手机排版留着这两格：
/// 数字是 0 但位置对得上，将来真做了离线下载不用重排一次布局。
/// </para>
///
/// <para>
/// 「继续观看」只做展示与跳转：点一张卡回到首页的「继续观看」视图，而不是在这里直接起播——
/// 播放页的连播队列属于首页那份列表，从这里插进去会把"上一个 / 下一个"的来源弄乱。
/// </para>
/// </summary>
public sealed class MineViewModel : ViewModelBase, IDisposable
{
    private readonly SourceSession? _session;
    private readonly ShellViewModel _host;
    private bool _loading;

    /// <param name="session">
    /// 当前源；<b>允许为 null</b>——一个源都没连上时「我的」也能打开（APK 里那个人像按钮
    /// 一直都在），只是四宫格全是 0、继续观看为空。给个"点了没反应"的按钮比给一张空表更糟。
    /// </param>
    public MineViewModel(SourceSession? session, ShellViewModel host)
    {
        _session = session;
        _host = host;

        Subtitle = session is null
            ? "还没有连接任何源"
            : $"{session.ItemCount:N0} 条 · {session.Entry.AddressLine}";
        SourceCountText = $"{host.Sessions.Count} 个远程源";

        BackCommand = new RelayCommand(() => _host.CloseMine());
        OpenLikedCommand = new RelayCommand(() =>
        {
            _host.CloseMine();
            _host.Home?.OpenFavoritesView();
        });
        OpenCollectionsCommand = new RelayCommand(() =>
        {
            _host.CloseMine();
            _host.Home?.OpenCollectionsView();
        });
        OpenContinueCommand = new RelayCommand(() =>
        {
            _host.CloseMine();
            _host.Home?.OpenRecentView();
        });
    }

    public string Avatar => "🦊";

    public string Title => "我的媒体库";

    public string Subtitle { get; }

    public string SourceCountText { get; }

    /// <summary>封面缓存：和海报墙共用一套解码逻辑，只是这里只有几张。</summary>
    public PosterCache Posters { get; } = new();

    public ObservableCollection<MediaCard> Continue { get; } = new();

    public RelayCommand BackCommand { get; }
    public RelayCommand OpenLikedCommand { get; }
    public RelayCommand OpenCollectionsCommand { get; }
    public RelayCommand OpenContinueCommand { get; }

    // ── 四宫格 ─────────────────────────────────────────────────────

    private string _liked = "0";
    private string _watched = "0";
    private int _likedCount;

    public string LikedText
    {
        get => _liked;
        private set => Set(ref _liked, value);
    }

    public string WatchedText
    {
        get => _watched;
        private set => Set(ref _watched, value);
    }

    /// <summary>桌面端没有下载功能：这两格恒为 0，照手机排版留着。</summary>
    public string DownloadText => "0";

    public string BytesText => "0 B";

    /// <summary>收藏入口的副标题要带上真实数字，所以单独暴露一个计数。</summary>
    public string LikedSubtitle => $"{_likedCount:N0} 个视频 · 服务端为准";

    public bool HasContinue => Continue.Count > 0;

    public bool IsLoading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (!Set(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(_error);

    // ── 加载 ───────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        if (_session is null) return;

        IsLoading = true;
        try
        {
            // 收藏数 = 各收藏夹条目数之和（跨源口径由服务端按门户收窄好了）
            try
            {
                var favorites = await _session.Api.FavoritesAsync().ConfigureAwait(true);
                _likedCount = favorites.Sum(f => f.ItemCount);
                LikedText = _likedCount.ToString("N0");
                OnPropertyChanged(nameof(LikedSubtitle));
            }
            catch (Exception e) when (e is ApiException or NetworkException)
            {
                ClientLog.Write($"我的页：读收藏夹失败 → {e.Message}");
            }

            // 看过 = 服务端按 last_play_at 倒序且"位置 > 0"的条目总数
            try
            {
                var page = await _session.Api.LibraryAsync(limit: 6, sort: "recent").ConfigureAwait(true);
                WatchedText = page.Total.ToString("N0");

                Continue.Clear();
                foreach (var dto in page.Items)
                {
                    // 继续观看恒画续播条：看 5 秒也算看过（与手机同一条判据）
                    Continue.Add(new MediaCard(dto, _session.Api, _ => OpenRecent(), alwaysShowProgress: true));
                }

                OnPropertyChanged(nameof(HasContinue));
            }
            catch (Exception e) when (e is ApiException or NetworkException)
            {
                ClientLog.Write($"我的页：读继续观看失败 → {e.Message}");
                Error = e.Message;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>点一张继续观看的卡：回首页的「继续观看」视图（那里才有连播队列）。</summary>
    private void OpenRecent() => Dispatcher.UIThread.Post(() => OpenContinueCommand.Execute(null));

    public void Dispose()
    {
        Continue.Clear();
        Posters.Dispose();
    }
}
