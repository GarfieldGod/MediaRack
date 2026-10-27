using System.Collections.ObjectModel;
using MediaRack.Client.Tools;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 主页的「合集」页（导航第三项）。
///
/// 两级结构：先网格（生产的库有 955 个合集，带过滤框），点一个进海报墙。
/// 合集是媒体的<b>投影</b>（single 看目录、dual 看 UP 主），只读、源级、平铺排他——
/// 所以这一页没有任何编辑动作，用户想"整理"请用收藏夹（见 <c>HomeViewModel.Favorites.cs</c>）。
///
/// 网格与海报墙共用 <see cref="ReloadAsync"/> 这个加载壳：错误条、载入态、条件去重都只有一份，
/// 靠 <see cref="ShowCollectionGrid"/> 决定这一次跑哪条分支。
/// </summary>
public sealed partial class HomeViewModel
{
    private readonly List<CollectionCard> _allCollections = new();

    /// <summary>合集网格的过滤词（客户端侧过滤：一份千把条的清单在内存里，没必要每敲一个字问服务端）。</summary>
    private string _collectionQuery = "";

    /// <summary>网格列数。固定 3 列：合集卡只有名字跟一行 meta，不需要像海报那样按宽度塞更多。</summary>
    private const int CollectionColumns = 3;

    public ObservableCollection<CollectionCard> CollectionCards { get; } = new();
    public ObservableCollection<CollectionRow> CollectionRows { get; } = new();

    public RelayCommand CollectionBackCommand { get; private set; } = null!;
    public AsyncRelayCommand ReloadCollectionsCommand { get; private set; } = null!;

    private void InitCollections()
    {
        CollectionBackCommand = new RelayCommand(BackToCollectionGrid);
        ReloadCollectionsCommand = new AsyncRelayCommand(ReloadCollectionGridAsync);
    }

    public string CollectionQuery
    {
        get => _collectionQuery;
        set
        {
            if (!Set(ref _collectionQuery, value)) return;
            RefreshCollectionView();
        }
    }

    public string CollectionCountText => _allCollections.Count == 0
        ? "载入中…"
        : $"{CollectionCards.Count:N0} / {_allCollections.Count:N0} 个合集";

    public string CollectionEmptyText =>
        _allCollections.Count > 0 && CollectionCards.Count == 0 && !IsLoading
            ? $"没有匹配「{_collectionQuery.Trim()}」的合集"
            : "";

    /// <summary>网格加载：第一次进来才真拉，之后靠刷新按钮（一次约 100 KB，但没必要每次切页都拉）。</summary>
    private async Task LoadCollectionGridAsync()
    {
        if (_allCollections.Count == 0)
        {
            var list = await _api.CollectionsAsync();
            _allCollections.Clear();
            // 条目多的排前面：用户找的多半是"那几个大 UP"，不是空目录
            foreach (var c in list.OrderByDescending(c => c.ItemCount))
            {
                _allCollections.Add(new CollectionCard(c, OpenCollection));
            }
        }

        RefreshCollectionView();
        ClientLog.Write($"合集网格：{_allCollections.Count} 个，显示 {CollectionCards.Count} 个，" +
                        $"{CollectionRows.Count} 行 × {CollectionColumns} 列");
    }

    private void RefreshCollectionView()
    {
        var q = _collectionQuery.Trim();

        CollectionCards.Clear();
        foreach (var c in _allCollections)
        {
            if (q.Length > 0 &&
                !c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !c.SourceId.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !c.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            CollectionCards.Add(c);
        }

        RebuildCollectionRows();
        OnPropertyChanged(nameof(CollectionCountText));
        OnPropertyChanged(nameof(CollectionEmptyText));
    }

    private void RebuildCollectionRows()
    {
        CollectionRows.Clear();
        for (var i = 0; i < CollectionCards.Count; i += CollectionColumns)
        {
            CollectionRows.Add(new CollectionRow(
                CollectionCards.Skip(i).Take(CollectionColumns).ToList(), CollectionColumns));
        }
    }

    private async Task ReloadCollectionGridAsync()
    {
        _allCollections.Clear();
        await ReloadAsync(alsoReloadNavLists: true);
    }
}
