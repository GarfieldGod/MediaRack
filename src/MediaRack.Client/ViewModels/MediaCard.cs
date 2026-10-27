using System.Globalization;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MediaRack.Client.Media;
using MediaRack.Client.Remote;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 海报墙里的一张卡片。展示层模型：契约 DTO 只管传输，格式化 / 懒加载 / 交互都在这儿做，
/// 将来换布局（列表、信息流）不动协议层。
/// </summary>
public sealed class MediaCard : ViewModelBase
{
    private readonly MediaSummaryDto _dto;
    private readonly bool _alwaysShowProgress;
    private Bitmap? _poster;
    private bool _posterRequested;

    /// <param name="alwaysShowProgress">
    /// 「继续观看」视图专用：只要播过一点就画续播条（看 5 秒也算）。
    /// 海报墙里不这样——那里 163 条里有 150 条是"点开就关"的，全画上进度等于满屏都是条。
    /// </param>
    public MediaCard(MediaSummaryDto dto, ApiClient api, Action<MediaCard> onOpen,
        bool alwaysShowProgress = false)
    {
        _dto = dto;
        _alwaysShowProgress = alwaysShowProgress;
        Key = dto.Key;
        SourceId = dto.SourceId;
        Title = string.IsNullOrWhiteSpace(dto.Title) ? "（无标题）" : dto.Title.Trim();
        UpName = dto.UpName ?? "";

        var poster = dto.PosterUrl is { Length: > 0 } p ? api.MediaUrl(p) : null;
        PosterUrl = poster;

        DurationMs = dto.DurationMs;
        SizeBytes = dto.SizeBytes;
        Width = dto.Width;
        Height = dto.Height;
        Status = dto.Status;
        CollectionName = dto.CollectionName ?? "";

        // 续播条：与 Android / Web 同一套判据（>30s 才算看过、剩 <20s 视为看完）
        ApplyProgress(dto.PlayPosMs, dto.DurationMs, completed: false);

        OpenCommand = new RelayCommand<MediaCard>(onOpen);
    }

    public string Key { get; }
    public string SourceId { get; }
    public string Title { get; }
    public string UpName { get; }
    public string? PosterUrl { get; }
    public long DurationMs { get; }
    public long SizeBytes { get; }
    public int Width { get; }
    public int Height { get; }
    public string Status { get; }
    public string CollectionName { get; }

    public ICommand OpenCommand { get; }

    /// <summary>异步解码出来的封面。null = 还没到 / 服务端没有封面（两者下屏都是占位块）。</summary>
    public Bitmap? Poster
    {
        get => _poster;
        private set
        {
            if (Set(ref _poster, value)) OnPropertyChanged(nameof(HasPoster));
        }
    }

    public bool HasPoster => _poster is not null;

    private double _progressPercent;
    private string? _progressText;

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => Set(ref _progressPercent, value);
    }

    public string? ProgressText
    {
        get => _progressText;
        private set
        {
            if (Set(ref _progressText, value)) OnPropertyChanged(nameof(HasProgress));
        }
    }

    public bool HasProgress => _progressText is not null;

    /// <summary>
    /// 就地更新续播条。播放页关掉时调用——不用整表重载（那会把用户滚了半天的位置弹回顶部）。
    /// 判据与构造时一致：不到 30 秒不标、剩不到 20 秒视为看完（不标）。
    /// </summary>
    public void ApplyProgress(long posMs, long durationMs, bool completed)
    {
        var minPos = _alwaysShowProgress ? 1L : 30_000L;
        if (completed || durationMs <= 0 || posMs < minPos || posMs >= durationMs - 20_000)
        {
            ProgressPercent = 0;
            ProgressText = null;
            return;
        }

        ProgressPercent = Math.Clamp(posMs * 100.0 / durationMs, 0, 100);
        ProgressText = $"看到 {FormatDuration(posMs)} / {FormatDuration(durationMs)}";
    }

    /// <summary>副行：时长 · 体积 · 分辨率。空片长（源里没探测到）就不显示时长，避免 "00:00"。</summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string>(3);
            if (DurationMs > 0) parts.Add(FormatDuration(DurationMs));
            if (SizeBytes > 0) parts.Add(FormatSize(SizeBytes));
            if (Width > 0 && Height > 0) parts.Add($"{Width}×{Height}");
            return string.Join(" · ", parts);
        }
    }

    public string SubLine => CollectionName.Length > 0 ? CollectionName
        : UpName.Length > 0 ? UpName
        : SourceId;

    /// <summary>missing / corrupt 的条目服务端默认不下发，但 includeMissing 时会露出来，给个角标。</summary>
    public bool IsBroken => Status is "missing" or "corrupt";

    public string BrokenText => Status == "missing" ? "文件缺失" : "文件损坏";

    /// <summary>
    /// 懒加载封面。由视图在图片控件 <c>Loaded</c> 时触发——列表是虚拟化的，
    /// 只有真正滚进视口的卡片才会发出请求，翻十页也不会把几百张图全拉一遍。
    /// </summary>
    public async Task EnsurePosterAsync(PosterCache cache)
    {
        if (_posterRequested || string.IsNullOrEmpty(PosterUrl)) return;
        _posterRequested = true;

        var bmp = await cache.GetAsync(PosterUrl).ConfigureAwait(false);
        if (bmp is null) return;

        if (Dispatcher.UIThread.CheckAccess()) Poster = bmp;
        else Dispatcher.UIThread.Post(() => Poster = bmp);
    }

    // ── 格式化（与 Android 端保持同一口径）──────────────────────────
    internal static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " B",
    };
}

/// <summary>
/// 海报墙的一行。不给外层用 WrapPanel 的原因是虚拟化：把卡片按列数切行后，
/// 外层列表只需实例化"视口内的行"，一页 60 条 = 15 行，翻到 600 条也只有几十行在渲染。
/// </summary>
public sealed class MediaRow(IReadOnlyList<MediaCard> cards, int columns)
{
    public IReadOnlyList<MediaCard> Cards { get; } = cards;
    public int Columns { get; } = columns;
}
