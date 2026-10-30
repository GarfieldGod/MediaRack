using System.Collections.ObjectModel;
using MediaRack.Client.Remote;
using MediaRack.Client.Tools;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 主页的「收藏」两件事：
///
/// 1. <b>收藏夹本身的增删改</b>——侧栏「收藏夹」标题旁的 ＋（新建）、
///    选中某个夹子时工具栏上的「重命名 / 删除」（删除要二次确认）。
/// 2. <b>把某一条放进/拿出收藏夹</b>——详情侧栏的 chips，点一下就是一次
///    <c>POST /favorites/{id}/items</c>，成功才翻转状态。
///
/// 收藏是<b>用户层</b>：多对多、可跨源、可跨端，与结构合集（源级只读投影）是两回事，
/// 不要互相套用。所有写操作都不动媒体文件，只改 favorite / favorite_item 两张表。
/// </summary>
public sealed partial class HomeViewModel
{
    private int _favMembershipGen;

    private bool _isCreatingFavorite;
    private string _newFavoriteName = "";

    private bool _isRenamingFavorite;
    private string _renameText = "";

    private bool _confirmDeleteFavorite;

    private string _favoriteHint = "";

    /// <summary>详情侧栏的收藏夹开关（当前选中条目属于哪些夹子）。</summary>
    public ObservableCollection<FavoriteChip> FavoriteChips { get; } = new();

    public RelayCommand BeginCreateFavoriteCommand { get; private set; } = null!;
    public AsyncRelayCommand CommitCreateFavoriteCommand { get; private set; } = null!;
    public RelayCommand CancelCreateFavoriteCommand { get; private set; } = null!;

    public RelayCommand BeginRenameFavoriteCommand { get; private set; } = null!;
    public AsyncRelayCommand CommitRenameFavoriteCommand { get; private set; } = null!;
    public RelayCommand CancelRenameFavoriteCommand { get; private set; } = null!;

    public RelayCommand BeginDeleteFavoriteCommand { get; private set; } = null!;
    public AsyncRelayCommand CommitDeleteFavoriteCommand { get; private set; } = null!;
    public RelayCommand CancelDeleteFavoriteCommand { get; private set; } = null!;

    private void InitFavorites()
    {
        BeginCreateFavoriteCommand = new RelayCommand(BeginCreateFavorite);
        CommitCreateFavoriteCommand = new AsyncRelayCommand(CommitCreateFavoriteAsync,
            () => _newFavoriteName.Trim().Length > 0);
        CancelCreateFavoriteCommand = new RelayCommand(() =>
        {
            IsCreatingFavorite = false;
            NewFavoriteName = "";
        });

        BeginRenameFavoriteCommand = new RelayCommand(BeginRenameFavorite);
        CommitRenameFavoriteCommand = new AsyncRelayCommand(CommitRenameFavoriteAsync,
            () => _renameText.Trim().Length > 0);
        CancelRenameFavoriteCommand = new RelayCommand(() =>
        {
            IsRenamingFavorite = false;
            ConfirmDeleteFavorite = false;
        });

        BeginDeleteFavoriteCommand = new RelayCommand(() =>
        {
            IsRenamingFavorite = false;
            ConfirmDeleteFavorite = true;
        });
        CommitDeleteFavoriteCommand = new AsyncRelayCommand(DeleteCurrentFavoriteAsync);
        CancelDeleteFavoriteCommand = new RelayCommand(() => ConfirmDeleteFavorite = false);
    }

    // ── 状态 ───────────────────────────────────────────────────────

    /// <summary>当前停在某个收藏夹里（工具栏据此决定显示不显示重命名/删除）。</summary>
    public bool IsInFavorite => _nav is { IsFavorite: true };

    public bool ShowFavoriteTools => IsInFavorite && !IsRenamingFavorite && !ConfirmDeleteFavorite;

    public bool IsCreatingFavorite
    {
        get => _isCreatingFavorite;
        set => Set(ref _isCreatingFavorite, value);
    }

    public string NewFavoriteName
    {
        get => _newFavoriteName;
        set
        {
            if (!Set(ref _newFavoriteName, value)) return;
            CommitCreateFavoriteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsRenamingFavorite
    {
        get => _isRenamingFavorite;
        private set
        {
            if (!Set(ref _isRenamingFavorite, value)) return;
            OnPropertyChanged(nameof(ShowFavoriteTools));
        }
    }

    public string RenameText
    {
        get => _renameText;
        set
        {
            if (!Set(ref _renameText, value)) return;
            CommitRenameFavoriteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ConfirmDeleteFavorite
    {
        get => _confirmDeleteFavorite;
        private set
        {
            if (!Set(ref _confirmDeleteFavorite, value)) return;
            OnPropertyChanged(nameof(ShowFavoriteTools));
        }
    }

    /// <summary>收藏动作的回执（"已加入「稍后再看」"这类），显示在详情侧栏底部。</summary>
    public string FavoriteHint
    {
        get => _favoriteHint;
        private set
        {
            if (Set(ref _favoriteHint, value)) OnPropertyChanged(nameof(HasFavoriteHint));
        }
    }

    public bool HasFavoriteHint => _favoriteHint.Length > 0;

    /// <summary>一条收藏夹都没有时给个提示，别让侧栏那块空着像坏了。</summary>
    public string FavoriteEmptyText =>
        FavoriteChips.Count == 0 ? "这个服务端还没有收藏夹（侧栏「收藏夹」旁的 ＋ 新建）" : "";

    // ── 新建 ───────────────────────────────────────────────────────

    private void BeginCreateFavorite()
    {
        IsCreatingFavorite = true;
        NewFavoriteName = "";
    }

    private async Task CommitCreateFavoriteAsync()
    {
        var name = _newFavoriteName.Trim();
        if (name.Length == 0) return;

        try
        {
            var fav = await _api.CreateFavoriteAsync(name);
            IsCreatingFavorite = false;
            NewFavoriteName = "";
            await ReloadNavListsAsync();

            // 建完直接进去：用户的意图就是"建一个用来放东西的夹子"
            var nav = FavoriteNavs.FirstOrDefault(n => n.Id == fav.Id);
            if (nav is not null) ApplyNav(nav);
            ClientLog.Write($"新建收藏夹：{fav.Name}（{fav.Id}）");
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Error = e.Message;
        }
    }

    // ── 重命名 / 删除 ──────────────────────────────────────────────

    private void BeginRenameFavorite()
    {
        if (_nav is not { IsFavorite: true } nav) return;
        RenameText = nav.Title;
        IsRenamingFavorite = true;
    }

    private async Task CommitRenameFavoriteAsync()
    {
        if (_nav is not { IsFavorite: true } nav) return;
        var name = _renameText.Trim();
        if (name.Length == 0 || name == nav.Title)
        {
            IsRenamingFavorite = false;
            return;
        }

        try
        {
            await _api.RenameFavoriteAsync(nav.Id!, name);
            IsRenamingFavorite = false;
            await ReloadNavListsAsync();
            ClientLog.Write($"重命名收藏夹：{nav.Title} → {name}");
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Error = e.Message;
        }
    }

    private async Task DeleteCurrentFavoriteAsync()
    {
        if (_nav is not { IsFavorite: true } nav) return;

        try
        {
            await _api.DeleteFavoriteAsync(nav.Id!);
            ConfirmDeleteFavorite = false;
            // 删掉的正是不当前这个夹子 —— 先退回「全部视频」，别停在一个已经不存在的视图上
            ApplyNav(SourceNavs[0]);
            await ReloadNavListsAsync();
            ClientLog.Write($"删除收藏夹：{nav.Title}（{nav.Id}）");
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            Error = e.Message;
        }
    }

    // ── 收藏到（详情侧栏）────────────────────────────────────────────

    /// <summary>
    /// 选中条目变了就重拉一次归属。用代数（gen）挡竞态：快速连点几张卡片时，
    /// 先发的请求可能后回来，得让晚到的结果自己作废。
    /// </summary>
    private void ReloadFavoriteChips(MediaCard? card)
    {
        var gen = ++_favMembershipGen;
        FavoriteHint = "";

        if (card is null)
        {
            FavoriteChips.Clear();
            OnPropertyChanged(nameof(FavoriteEmptyText));
            return;
        }

        _ = LoadFavoriteChipsAsync(card, gen);
    }

    private async Task LoadFavoriteChipsAsync(MediaCard card, int gen)
    {
        List<string> mine;
        try
        {
            mine = await _api.FavoritesOfMediaAsync(card.SourceId, card.Key);
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            ClientLog.Write($"收藏归属加载失败 {card.SourceId}/{card.Key}：{e.Message}");
            return;
        }

        if (gen != _favMembershipGen) return; // 已经切到别的条目了

        FavoriteChips.Clear();
        foreach (var nav in FavoriteNavs)
        {
            FavoriteChips.Add(new FavoriteChip(nav.Id!, nav.Title, mine.Contains(nav.Id!), ToggleFavoriteAsync));
        }

        OnPropertyChanged(nameof(FavoriteEmptyText));
    }

    private async Task ToggleFavoriteAsync(FavoriteChip chip)
    {
        var card = _selectedCard;
        // 这行日志是"点了没反应"时唯一能分辨原因的地方：
        // 有它说明命令到了、是网络/服务端的问题；没它说明点击没打到按钮。
        ClientLog.Write($"收藏切换点击：{chip.Name}（当前 on={chip.On}）条目={card?.SourceId}/{card?.Key}");
        if (card is null) return;

        var item = new ItemRefBody { Source = card.SourceId, Key = card.Key };
        try
        {
            if (chip.On)
            {
                await _api.FavoriteItemsAsync(chip.Id, remove: [item]);
                chip.On = false;
                FavoriteHint = $"已从「{chip.Name}」移出";
            }
            else
            {
                await _api.FavoriteItemsAsync(chip.Id, add: [item]);
                chip.On = true;
                FavoriteHint = $"已加入「{chip.Name}」";
            }

            // 夹子的条目数变了，侧栏那行"N 条"要跟着变（不整表重载：重载会把滚动位置弹回顶部）
            await ReloadNavListsAsync();
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            FavoriteHint = e.Message;
            ClientLog.Write($"收藏切换失败 {chip.Id}：{e.Message}");
        }
    }
}
