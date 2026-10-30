namespace MediaRack.Client.ViewModels;

/// <summary>
/// 左侧导航的一项。
///
/// Kind：
/// <c>all</c> 全部视频（聚合视图，跨源去重）/ <c>source</c> 单个源 /
/// <c>favorite</c> 收藏夹 / <c>recent</c> 继续观看 / <c>collections</c> 合集页（网格）。
///
/// 点进某个合集后不再造新的导航项：合集页原地换成海报墙，工具栏给返回。
///
/// 合集不进侧栏平铺——生产的库里 955 个合集（602 个 UP + 353 个目录），
/// 平铺出来等于没法用。所以它有一层自己的网格页，点进去才变成海报墙。
/// </summary>
public sealed class NavItem(string kind, string? id, string title, string subtitle = "", string? sourceId = null)
{
    public string Kind { get; } = kind;
    public string? Id { get; } = id;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;

    /// <summary>合集视图用：这个合集属于哪个源（合集 id 里虽然带着源名，但不该在客户端解析字符串）。</summary>
    public string? SourceId { get; } = sourceId;

    public bool IsAll => Kind == "all";
    public bool IsFavorite => Kind == "favorite";
    public bool IsRecent => Kind == "recent";
    public bool IsCollections => Kind == "collections";
}

/// <summary>合集下拉的一项（含开头的「全部合集」）。</summary>
public sealed class CollectionOption(string? id, string label, int itemCount)
{
    public string? Id { get; } = id;
    public string Label { get; } = label;
    public int ItemCount { get; } = itemCount;

    public string Display => Id is null ? Label : $"{Label}（{ItemCount}）";
}

/// <summary>
/// 合集页里的一张卡。合集是只读的结构层（源级、平铺、排他），
/// 所以这里没有编辑动作，只有"点进去看"。
/// </summary>
public sealed class CollectionCard(Remote.RemoteCollectionDto dto, Action<CollectionCard> onOpen)
{
    public string Id { get; } = dto.Id;
    public string Name { get; } = string.IsNullOrWhiteSpace(dto.Name) ? "（未命名）" : dto.Name;
    public string SourceId { get; } = dto.SourceId;
    public string Kind { get; } = dto.Kind;
    public string Path { get; } = dto.Path;
    public int ItemCount { get; } = dto.ItemCount;

    /// <summary>kind=up 是"某个 UP 主的全部投稿"，kind=folder 是目录合集——两种在画面上的区别就这一行字。</summary>
    public string KindText => Kind switch
    {
        "up" => "UP 主",
        _ => "目录",
    };

    public string Detail => $"{SourceId} · {KindText} · {ItemCount:N0} 条";

    public RelayCommand<CollectionCard> OpenCommand { get; } = new(onOpen);
}

/// <summary>
/// 合集网格的一行。与海报墙同一个套路（按列数切行 + 外层 ListBox 虚拟化），
/// 956 个合集才不会一次性实例化几千个控件。行内用 UniformGrid 均分宽度，
/// 卡片自己不用算宽度。
/// </summary>
public sealed class CollectionRow(IReadOnlyList<CollectionCard> cards, int columns)
{
    public IReadOnlyList<CollectionCard> Cards { get; } = cards;
    public int Columns { get; } = columns;
}
