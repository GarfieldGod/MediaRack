using Avalonia.Threading;
using LibVLCSharp.Shared;
using MediaRack.Client.Playback;
using MediaRack.Client.Remote;
using MediaRack.Client.Tools;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace MediaRack.Client.ViewModels;

/// <summary>播放页关掉时带给列表的信息（列表拿它就地更新卡片上的续播条）。</summary>
public sealed class PlayClosedEventArgs(string sourceId, string key, long positionMs, long durationMs, bool completed)
    : EventArgs
{
    public string SourceId { get; } = sourceId;
    public string Key { get; } = key;
    public long PositionMs { get; } = positionMs;
    public long DurationMs { get; } = durationMs;
    public bool Completed { get; } = completed;
}

/// <summary>
/// 播放页。一个实例管一辈子（第一次点播放时创建，之后换条目只换 Media），
/// 这样 <c>VideoView</c> 的 <c>MediaPlayer</c> 只绑一次，切条不黑屏、不重建渲染管线。
///
/// <para><b>取流是三级降级的</b>（与 APK 端同一套判据）：</para>
/// <list type="number">
/// <item>layout=dual → video.m4s 为主 + audio.m4s 用 <c>:input-slave</c> 客户端合成
///   （等价 Media3 的 MergingMediaSource）</item>
/// <item>layout=single → 单个 mp4/mkv 直接播</item>
/// <item>直连报错 / 起播超时 → 服务端 ffmpeg 转封装成单文件（B 站 HEVC 常把参数集放在分片里，
///   hvcC 的 numOfArrays=0，直连解析会失败）</item>
/// </list>
///
/// <para><b>状态推进全在 UI 定时轮询里做，不订阅 VLC 事件。</b>VLC 的回调在自己的线程上，
/// 在里面碰 UI 属性要手写 Dispatcher 转发、还要处理"回调比页面活得久"的竞态；
/// 每 400ms 读一次 State/Time/Length 三个原子属性，代价可以忽略，逻辑只有一条线程。</para>
/// </summary>
public sealed class PlayerViewModel : ViewModelBase, IDisposable
{
    /// <summary>与 APK 端同一口径：看不过 30 秒不算"看过"，剩不到 20 秒不算"看了一半"。</summary>
    private const long ResumeMinMs = 30_000;
    private const long CompleteTailMs = 20_000;

    private const int ReportIntervalMs = 5_000;
    private const int StartupTimeoutMs = 9_000;

    private readonly ApiClient _api;
    private readonly LibVLC? _libvlc;
    private readonly MediaPlayer? _player;
    private readonly DispatcherTimer? _timer;

    private VlcMedia? _media;
    private MediaDetailDto? _detail;

    private string _sourceId = "";
    private string _key = "";
    private string _title = "";
    private string _subtitle = "";
    private string _engineLabel = "";
    private string _mode = "";
    private string _error = "";
    private string _status = "";
    private string _hint = "";

    private long _durationMs;
    private long _positionMs;
    private long _resumeMs;
    private double _seekPercent;
    private double _volume = 80;
    private double _volumeBeforeMute = 80;

    private bool _isPlaying;
    private bool _isLoading;
    private bool _isOpen;
    private bool _started;
    private bool _playRequested;
    private bool _endHandled;
    private bool _resumeApplied;
    private bool _triedRemux;
    private bool _scrubbing;
    private bool _suppressSeek;
    private bool _disposed;

    private int _playGen;
    private long _playStartTick;
    private long _lastReportTick;
    private long _lastReportedMs;
    private Task _lastReport = Task.CompletedTask;

    // ── 连播队列 / 播放模式 / 画面 ──────────────────────────────────
    private readonly List<PlayQueueItem> _queue = new();
    private int _queueIndex = -1;
    private string _queueLabel = "";
    private PlayMode _playMode = PlayMode.PauseAtEnd;
    private double _speed = 1.0;
    private bool _muted;
    private bool _cover;
    private string _viewportAspect = "16:9";
    private bool _isFullscreen;

    public PlayerViewModel(ApiClient api)
    {
        _api = api;

        _libvlc = PlaybackEngine.Get();
        if (_libvlc is null)
        {
            _error = "播放引擎不可用：" + (PlaybackEngine.Error ?? "未知原因");
            ClientLog.Write("播放页：引擎缺失，只有浏览可用");
        }
        else
        {
            _player = new MediaPlayer(_libvlc) { EnableHardwareDecoding = true };
            _player.Volume = (int)_volume;
            // 400ms 足够让进度条看起来是连续的，又不至于把 UI 线程刷爆
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) => Pump());
        }

        TogglePlayCommand = new RelayCommand(TogglePlay);
        CloseCommand = new RelayCommand(Close);
        Back10Command = new RelayCommand(() => Skip(-10_000));
        Forward10Command = new RelayCommand(() => Skip(10_000));
        RetryCommand = new AsyncRelayCommand(() => StartAsync(remuxOnly: _triedRemux), () => _player is not null);
        NextCommand = new RelayCommand(Next);
        PrevCommand = new RelayCommand(Prev);
        CycleModeCommand = new RelayCommand(() => Mode = _playMode.Next());
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        ToggleCoverCommand = new RelayCommand(ToggleCover);
        ToggleFullscreenCommand = new RelayCommand(() => FullscreenRequested?.Invoke(this, EventArgs.Empty));
        CycleSpeedCommand = new RelayCommand(() => SpeedIndex = (SpeedIndex + 1) % SpeedSteps.Length);
    }

    /// <summary>页面关闭（用户点返回 / 列表切页）。位置信息回给列表更新续播条。</summary>
    public event EventHandler<PlayClosedEventArgs>? Closed;

    /// <summary>请求切全屏。窗口状态归视图管（VM 不碰 Window），这里只发意图。</summary>
    public event EventHandler? FullscreenRequested;

    public RelayCommand TogglePlayCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand Back10Command { get; }
    public RelayCommand Forward10Command { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand CycleModeCommand { get; }
    public RelayCommand CycleSpeedCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand ToggleCoverCommand { get; }
    public RelayCommand ToggleFullscreenCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }

    // ── 播放内容 ───────────────────────────────────────────────────

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    /// <summary>当前在播的条目 key（自检断言"真的换条了"用）。</summary>
    public string CurrentKey => _key;

    /// <summary>诊断行：`双流直连 · HEVC 2160×3840`。播放出问题时第一个要看的就是它。</summary>
    public string EngineLabel { get => _engineLabel; private set => Set(ref _engineLabel, value); }

    /// <summary>给视图绑定的播放器。视图只在 Loaded 时取一次。</summary>
    public MediaPlayer? MediaPlayer => _player;

    /// <summary>最近一次进度上报的任务。自检用它等"写完没"，生产代码不看。</summary>
    public Task LastReport => _lastReport;

    public long PositionMs => _positionMs;
    public long DurationMs => _durationMs;

    public string TimeText => $"{Fmt(_positionMs)} / {(_durationMs > 0 ? Fmt(_durationMs) : "--:--")}";
    public bool IsSeekable => _durationMs > 0;

    // ── 页面状态 ───────────────────────────────────────────────────

    /// <summary>页面是否展示中。列表用它在海报墙上叠一层播放页（保留滚动位置）。</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => Set(ref _isOpen, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value)) OnPropertyChanged(nameof(ShowCenter));
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (Set(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayButtonText));
        }
    }

    public string PlayButtonText => _isPlaying ? "暂停" : "播放";

    public string Error
    {
        get => _error;
        private set
        {
            if (!Set(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowCenter));
            OnPropertyChanged(nameof(CenterText));
        }
    }

    public bool HasError => _error.Length > 0;

    /// <summary>播放中的一行小状态（"服务端正在转封装…" / "已播完"）。</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(ShowCenter));
            OnPropertyChanged(nameof(CenterText));
        }
    }

    public bool HasStatus => _status.Length > 0;

    /// <summary>续播提示（"从 12:34 续播"），5 秒后自己消失，不占版面。</summary>
    public string Hint
    {
        get => _hint;
        private set
        {
            if (Set(ref _hint, value)) OnPropertyChanged(nameof(HasHint));
        }
    }

    public bool HasHint => _hint.Length > 0;

    public bool ShowCenter => _isLoading || HasError || HasStatus;

    public string CenterText => HasError ? _error : _status.Length > 0 ? _status : "正在连接…";

    // ── 控制 ───────────────────────────────────────────────────────

    /// <summary>进度条位置（0–100）。用户拖动时只改值不 seek，松手才 seek——见 <see cref="BeginScrub"/>。</summary>
    public double SeekPercent
    {
        get => _seekPercent;
        set
        {
            if (!Set(ref _seekPercent, value)) return;
            if (_suppressSeek || _scrubbing) return;
            SeekToMs((long)(value / 100.0 * _durationMs));
        }
    }

    /// <summary>音量 0–100，进程内记忆（换条目保持）。</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value)) return;

            // 静音状态下用户主动拉音量 = 想听声音了，顺手解除静音（否则拖了没反应像坏了）
            if (_muted && value > 0)
            {
                _muted = false;
                _volumeBeforeMute = value;
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(MuteText));
            }

            if (_player is not null) _player.Volume = (int)value;
        }
    }

    public void BeginScrub() => _scrubbing = true;

    public void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        if (_durationMs > 0) SeekToMs((long)(_seekPercent / 100.0 * _durationMs));
    }

    public void TogglePlay()
    {
        if (_player is null)
        {
            if (!HasError) Error = "播放引擎不可用：" + (PlaybackEngine.Error ?? "未知原因");
            return;
        }

        if (_endHandled)
        {
            // 播完后再点播放 = 从头再看（VLC 在 Ended 状态下 Play 会自己重来，这里显式归零更可控）
            _endHandled = false;
            Status = "";
            SeekToMs(0);
            _player.Play();
            return;
        }

        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    public void Skip(long deltaMs)
    {
        if (_player is null || _durationMs <= 0) return;
        SeekToMs(_positionMs + deltaMs);
    }

    /// <summary>音量加减（键盘 ↑↓ / 滚轮）。返回新值，方便视图显示。</summary>
    public double AdjustVolume(double delta)
    {
        Volume = Math.Clamp(_volume + delta, 0, 100);
        if (_muted && _volume > 0) ToggleMute();
        return _volume;
    }

    // ── 连播队列（上一个 / 下一个）──────────────────────────────────

    /// <summary>队列里的总条数（自检用；界面只看 <see cref="QueueText"/>）。</summary>
    public int QueueCount => _queue.Count;

    /// <summary>当前在队列里的位置，-1 = 不在队列里（直接 Open 的）。</summary>
    public int QueueIndex => _queueIndex;

    public bool HasPrev => _queueIndex > 0;

    public bool HasNext => _queueIndex >= 0 && _queueIndex < _queue.Count - 1;

    /// <summary>`连播：稍后再看 · 3/12`。没有队列时为空串，界面据此整块隐藏。</summary>
    public string QueueText =>
        _queue.Count > 1 && _queueIndex >= 0
            ? $"{_queueLabel} · {_queueIndex + 1}/{_queue.Count}"
            : "";

    public bool HasQueue => QueueText.Length > 0;

    /// <summary>
    /// 带着整份列表打开：这是"在收藏夹/合集里连着看"的唯一实现方式。
    /// 只把被点的那一条当孤立条目播，往下翻就没有上下文了（APK 端踩过，见 PlayQueue 注释）。
    /// </summary>
    public async Task PlayAsync(IReadOnlyList<PlayQueueItem> items, int index, string label)
    {
        _queue.Clear();
        _queue.AddRange(items);
        _queueLabel = label;
        _queueIndex = Math.Clamp(index, 0, Math.Max(0, _queue.Count - 1));

        if (_queue.Count == 0)
        {
            ClientLog.Write("连播队列为空，无法打开播放页");
            return;
        }

        await OpenAtAsync(_queueIndex);
    }

    public void Next()
    {
        if (HasNext) _ = OpenAtAsync(_queueIndex + 1);
    }

    public void Prev()
    {
        if (HasPrev) _ = OpenAtAsync(_queueIndex - 1);
    }

    private async Task OpenAtAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        var item = _queue[index];
        _queueIndex = index;
        RaiseQueueChanged();
        await OpenAsync(item.SourceId, item.Key, item.Title, item.Subtitle);
    }

    private void RaiseQueueChanged()
    {
        OnPropertyChanged(nameof(QueueCount));
        OnPropertyChanged(nameof(QueueIndex));
        OnPropertyChanged(nameof(HasPrev));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(HasQueue));
    }

    // ── 播放模式（播完暂停 / 循环播放 / 自动切集）────────────────────

    public PlayMode Mode
    {
        get => _playMode;
        set
        {
            if (!Set(ref _playMode, value)) return;
            OnPropertyChanged(nameof(ModeText));
            ClientLog.Write($"播放模式 → {value.Label()}");
        }
    }

    public string ModeText => _playMode.Label();

    // ── 倍速 ───────────────────────────────────────────────────────

    public static readonly double[] SpeedSteps = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    /// <summary>下拉框选中项。落回 <see cref="SpeedSteps"/> 的下标，越界一律当 1.0×。</summary>
    public int SpeedIndex
    {
        get
        {
            var i = Array.IndexOf(SpeedSteps, _speed);
            return i >= 0 ? i : 2;
        }
        set
        {
            var i = Math.Clamp(value, 0, SpeedSteps.Length - 1);
            if (Math.Abs(SpeedSteps[i] - _speed) < 0.001) return;
            SetSpeed(SpeedSteps[i]);
        }
    }

    public double Speed => _speed;

    public string SpeedText => $"{_speed:0.##}×";

    /// <summary>引擎实际生效的速率（自检用：光改 VM 里的数字不算数，要 libvlc 认）。</summary>
    public double EngineRate
    {
        get
        {
            try { return _player?.Rate ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>引擎实际音量（自检用：静音 = 音量 0）。</summary>
    public double EngineVolume
    {
        get
        {
            try { return _player?.Volume ?? -1; }
            catch { return -1; }
        }
    }

    public void SetSpeed(double speed)
    {
        _speed = speed;
        ApplySpeed();
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(SpeedIndex));
        ClientLog.Write($"倍速 → {SpeedText}");
    }

    private void ApplySpeed()
    {
        if (_player is null) return;
        try { _player.SetRate((float)_speed); }
        catch (Exception e) { ClientLog.Write("设置倍速失败：" + e.Message); }
    }

    // ── 静音 / 画面适配 / 全屏 ─────────────────────────────────────

    public bool IsMuted => _muted;

    public string MuteText => _muted ? "取消静音" : "静音";

    /// <summary>
    /// 静音走"音量归零 + 记下原值"，不用 <c>MediaPlayer.Mute</c>：
    /// Mute 在部分音频输出（含 <c>--aout=dummy</c>）上压根不回报状态，
    /// 表现为"点了没反应、再点也取消不了"；音量归零则任何 aout 都生效，还能断言。
    /// </summary>
    public void ToggleMute()
    {
        _muted = !_muted;

        if (_muted)
        {
            _volumeBeforeMute = _volume > 0 ? _volume : 80;
            if (_player is not null)
            {
                _player.Volume = 0;
                _player.Mute = true;
            }
        }
        else
        {
            if (_player is not null)
            {
                _player.Mute = false;
                _player.Volume = (int)_volumeBeforeMute;
            }
            // 音量为 0 的用户"取消静音"后得听得见，回填到静音前的值
            _volume = _volumeBeforeMute;
            OnPropertyChanged(nameof(Volume));
        }

        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(MuteText));
    }

    /// <summary>false = 适应窗口（整幅可见）/ true = 铺满裁边。与 APK 同一语义。</summary>
    public bool Cover => _cover;

    public string FitText => _cover ? "铺满" : "适应";

    public void ToggleCover()
    {
        _cover = !_cover;
        ApplyCover();
        OnPropertyChanged(nameof(Cover));
        OnPropertyChanged(nameof(FitText));
    }

    /// <summary>
    /// 铺满靠 libvlc 的 crop 几何：把源画面裁成"视口比例"，再缩放到填满窗口 = 裁边不变形。
    /// 视口比例由视图在尺寸变化时送进来（拿不到就退回 16:9，总比不生效强）。
    /// </summary>
    public void SetViewportSize(double width, double height)
    {
        if (width < 8 || height < 8) return;
        var g = Gcd((int)Math.Round(width), (int)Math.Round(height));
        var aspect = $"{Math.Round(width) / g}:{Math.Round(height) / g}";
        if (aspect == _viewportAspect) return;
        _viewportAspect = aspect;
        if (_cover) ApplyCover();
    }

    private void ApplyCover()
    {
        if (_player is null) return;

        try
        {
            // Cover = 先按视口比例裁剪源画面（裁掉超出的边），再把裁后的比例声明成显示比例
            // → 填满窗口且不变形。只裁不声明的话 libvlc 仍按原比例缩放，两侧照样留黑边。
            if (_cover)
            {
                _player.CropGeometry = _viewportAspect;
                _player.AspectRatio = _viewportAspect;
            }
            else
            {
                _player.CropGeometry = null;
                _player.AspectRatio = null;
            }
        }
        catch (Exception e)
        {
            ClientLog.Write("设置画面适配失败：" + e.Message);
        }
    }

    private static int Gcd(int a, int b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b > 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }

    public bool IsFullscreen => _isFullscreen;

    public string FullscreenText => _isFullscreen ? "退出全屏" : "全屏";

    /// <summary>窗口那边切完全屏后回填状态（VM 不碰 Window，只维护显示用的这一位）。</summary>
    public void SetFullscreenState(bool on)
    {
        if (_isFullscreen == on) return;
        _isFullscreen = on;
        OnPropertyChanged(nameof(IsFullscreen));
        OnPropertyChanged(nameof(FullscreenText));
        // 全屏后视口变了，铺满要按新比例重裁
        if (_cover) ApplyCover();
    }

    // ── 打开 / 关闭 ────────────────────────────────────────────────

    /// <summary>打开一条并起播。换条目也走这里（同一个播放器实例，不重建）。</summary>
    public async Task OpenAsync(string sourceId, string key, string title, string subtitle)
    {
        if (_disposed) return;

        // 换条目：先把上一条的进度落库，别让"连看三条只记住最后一条"
        if (_isOpen && (_sourceId != sourceId || _key != key)) FlushProgress();

        _sourceId = sourceId;
        _key = key;
        Title = title;
        Subtitle = subtitle;
        IsOpen = true;
        _triedRemux = false;

        await StartAsync(remuxOnly: false);
    }

    public void Close()
    {
        if (!_isOpen) return;

        var pos = _positionMs;
        // 已经播完的条目一律按"看完"处理：服务端据此把游标归零
        var completed = _endHandled || IsComplete(pos);
        if (!_endHandled) _ = ReportAsync(pos, completed);

        _timer?.Stop();
        IsOpen = false;
        IsLoading = false;
        IsPlaying = false;
        Status = "";
        Hint = "";

        // 同步 Stop：异步 Stop 会跟"关掉后立刻点开另一条"的 Play 打架，
        // 两条流各停一次的结果是新的那条刚起播就被停掉（表现为黑屏、再点一次才好）。
        // VLC 停局域网流通常是几十毫秒，代价可以接受。
        try { _player?.Stop(); }
        catch (Exception e) { ClientLog.Write("关闭播放器失败：" + e.Message); }

        Closed?.Invoke(this, new PlayClosedEventArgs(_sourceId, _key, pos, _durationMs, completed));
    }

    // ── 起播 ───────────────────────────────────────────────────────

    private async Task StartAsync(bool remuxOnly)
    {
        var gen = ++_playGen;

        if (_player is null)
        {
            Error = "播放引擎不可用：" + (PlaybackEngine.Error ?? "未知原因");
            IsLoading = false;
            return;
        }

        Error = "";
        Status = "";
        Hint = "";
        IsLoading = true;
        _started = false;
        // 准备阶段（拉详情 / 等转封装）不参与"起播超时"计时：
        // 否则拉详情的这几百毫秒会被当成 VLC 卡住，直接误判成解码失败。
        _playRequested = false;
        _endHandled = false;
        _resumeApplied = false;
        _suppressSeek = true;
        try { SeekPercent = 0; } finally { _suppressSeek = false; }
        _positionMs = 0;
        _durationMs = 0;
        _lastReportedMs = 0;
        OnPropertyChanged(nameof(PositionMs));
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(TimeText));

        StopCurrent();

        MediaDetailDto detail;
        try
        {
            detail = await _api.MediaDetailAsync(_sourceId, _key);
        }
        catch (Exception e) when (e is ApiException or NetworkException)
        {
            if (gen == _playGen) { Error = e.Message; IsLoading = false; }
            return;
        }

        // await 期间用户可能已经点了别的条目，这一份结果作废
        if (gen != _playGen || _disposed) return;

        _detail = detail;
        _durationMs = detail.DurationMs;
        _resumeMs = remuxOnly ? 0 : PickResumeMs(detail);

        var codec = string.IsNullOrEmpty(detail.Codec) ? "" : detail.Codec.ToUpperInvariant();
        var res = detail.Width > 0 && detail.Height > 0 ? $" {detail.Width}×{detail.Height}" : "";
        EngineLabel = (codec + res).Trim();

        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(IsSeekable));

        if (remuxOnly)
        {
            await StartRemuxAsync(gen);
            return;
        }

        var direct = BuildDirectPlan(detail);
        if (direct is not null)
        {
            StartVlc(direct, gen);
            return;
        }

        if (detail.Delivery.Contains("remux"))
        {
            await StartRemuxAsync(gen);
            return;
        }

        Error = "该条目没有可用的播放方式（delivery=" + string.Join("/", detail.Delivery) + "）";
        IsLoading = false;
    }

    /// <summary>
    /// 直连计划：dual 优先双流（缺音轨就只播视频），single 播单文件。
    /// 返回 null = 直连不可用，交给转封装兜底。
    /// </summary>
    private PlayPlan? BuildDirectPlan(MediaDetailDto detail)
    {
        if (!detail.Delivery.Contains("direct")) return null;

        if (detail.Layout == "dual")
        {
            var video = detail.Tracks.FirstOrDefault(t => t.Role == "video")?.Url;
            if (string.IsNullOrEmpty(video)) return null;
            var audio = detail.Tracks.FirstOrDefault(t => t.Role == "audio")?.Url;
            return new PlayPlan(
                _api.MediaUrl(video),
                string.IsNullOrEmpty(audio) ? null : _api.MediaUrl(audio),
                "双流直连");
        }

        var single = _api.SingleStreamUrl(detail);
        return single is null ? null : new PlayPlan(single, null, "单文件直连");
    }

    private async Task StartRemuxAsync(int gen)
    {
        if (_player is null) return;

        _triedRemux = true;
        Status = "服务端正在转封装…";
        ClientLog.Write($"转封装兜底：{_sourceId}/{_key}");

        var url = _api.RemuxUrl(_sourceId, _key);
        bool ready;
        try
        {
            // 202 = 服务端正在做；4K 双流现场转封装可能要几十秒
            ready = await _api.RemuxReadyAsync(url, 180_000);
        }
        catch (Exception e)
        {
            if (gen != _playGen) return;
            Error = "转封装请求失败：" + e.Message;
            IsLoading = false;
            return;
        }

        if (gen != _playGen || _disposed) return;

        if (!ready)
        {
            Error = "转封装失败或超时（服务端没有 ffmpeg，或这条转不了）";
            IsLoading = false;
            return;
        }

        StartVlc(new PlayPlan(url, null, "转封装"), gen);
    }

    private void StartVlc(PlayPlan plan, int gen)
    {
        if (_player is null || _libvlc is null || gen != _playGen) return;

        _mode = plan.Mode;
        _engineLabel = _engineLabel.Length > 0 ? $"{plan.Mode} · {_engineLabel}" : plan.Mode;
        OnPropertyChanged(nameof(EngineLabel));

        try
        {
            _media = new VlcMedia(_libvlc, new Uri(plan.Url));
            if (plan.Slave is { } slave) _media.AddOption(":input-slave=" + slave);
            // 局域网 1.5 秒够用；再大拖进度条就开始"按了不动"
            _media.AddOption(":network-caching=1500");
            _media.AddOption(":http-reconnect=true");
        }
        catch (Exception e)
        {
            Error = "无法创建播放会话：" + e.Message;
            IsLoading = false;
            return;
        }

        _playRequested = true;
        _playStartTick = Environment.TickCount64;
        _lastReportTick = _playStartTick;
        _player.Play(_media);
        _timer?.Start();

        // 换 media 会把 rate / crop / mute 打回默认，这里按用户当前选择补一遍。
        // rate 在真正 Playing 之前设不一定生效，起播成功时（ApplyResume）还会再来一次。
        ApplySpeed();
        ApplyCover();
        if (_player.Mute != _muted) _player.Mute = _muted;

        ClientLog.Write($"起播[{plan.Mode}] {_sourceId}/{_key} " +
                        $"← {plan.Url}{(plan.Slave is null ? "" : " + audio")}" +
                        (_resumeMs > 0 ? $"，续播 {_resumeMs}ms" : ""));
    }

    private void StopCurrent()
    {
        try { _player?.Stop(); }
        catch (Exception e) { ClientLog.Write("停止上一条失败：" + e.Message); }

        _media?.Dispose();
        _media = null;
    }

    // ── 轮询 ───────────────────────────────────────────────────────

    /// <summary>UI 定时器回调，也是无头自检的驱动入口（<c>--playtest</c> 里没有消息循环）。</summary>
    public void Pump()
    {
        if (_player is null || !_isOpen || _disposed) return;

        var state = _player.State;

        // Length 在首帧出来前是 0/-1；拿到真实值后以引擎为准（服务端探测的时长可能缺）
        var len = _player.Length;
        if (len > 1000 && Math.Abs(len - _durationMs) > 500)
        {
            _durationMs = len;
            OnPropertyChanged(nameof(DurationMs));
            OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(IsSeekable));
        }

        var t = _player.Time;
        if (t < 0)
        {
            var p = _player.Position;
            if (p > 0 && _durationMs > 0) t = (long)(p * _durationMs);
        }
        if (t < 0) t = 0;

        IsPlaying = _player.IsPlaying;

        if (!_started)
        {
            if (t > 0 && state is VLCState.Playing or VLCState.Paused)
            {
                _started = true;
                IsLoading = false;
                Status = "";
                ApplyResume();
                // 起播之后 rate 才稳得住（媒体还没 Playing 时设会被 demux 忽略）
                ApplySpeed();
            }
            else if (state == VLCState.Error)
            {
                OnPlaybackFailed("解码失败");
                return;
            }
            else if (_playRequested && Environment.TickCount64 - _playStartTick > StartupTimeoutMs)
            {
                // 有些坏参数的 HEVC 不会报错，只是永远卡在 Opening —— 只能靠超时兜底
                OnPlaybackFailed("起播超时");
                return;
            }
        }

        // 播完之后引擎的 Time 会飘回最后一帧的时间戳（比 Length 小几百毫秒），
        // 这里不再采信，否则"已播完"的进度会被改回"看到 99%"，下次又从片尾续播。
        if (!_endHandled)
        {
            if (t != _positionMs) UpdatePosition(t);
            MaybeReport(t);
        }

        if (state == VLCState.Ended && !_endHandled) OnEnded();
    }

    private void UpdatePosition(long t)
    {
        _positionMs = t;
        OnPropertyChanged(nameof(PositionMs));
        OnPropertyChanged(nameof(TimeText));

        if (_durationMs <= 0) return;
        _suppressSeek = true;
        try { SeekPercent = Math.Clamp(t * 100.0 / _durationMs, 0, 100); }
        finally { _suppressSeek = false; }
    }

    private void ApplyResume()
    {
        if (_resumeApplied || _resumeMs <= 0) return;
        _resumeApplied = true;
        SeekToMs(_resumeMs);
        Hint = $"从 {Fmt(_resumeMs)} 续播";
        ClientLog.Write($"续播 {_sourceId}/{_key} @ {_resumeMs}ms");
    }

    private void OnPlaybackFailed(string why)
    {
        ClientLog.Write($"播放失败（{why}）{_sourceId}/{_key}，模式={_mode}，state={_player?.State}");

        if (!_triedRemux && _detail is { } d && d.Delivery.Contains("remux"))
        {
            _ = StartAsync(remuxOnly: true);
            return;
        }

        _started = false;
        _playRequested = false;
        IsLoading = false;
        Status = "";
        IsPlaying = false;
        Error = $"播放失败：{why}（{_mode}）";
    }

    private void OnEnded()
    {
        _endHandled = true;
        IsPlaying = false;
        IsLoading = false;
        _positionMs = _durationMs;
        OnPropertyChanged(nameof(PositionMs));
        OnPropertyChanged(nameof(TimeText));
        _suppressSeek = true;
        try { SeekPercent = 100; } finally { _suppressSeek = false; }
        _ = ReportAsync(_durationMs, completed: true);
        ClientLog.Write($"播完 {_sourceId}/{_key}（{_durationMs}ms），模式={_playMode.Label()}，有下一条={HasNext}");

        switch (_playMode.DecideEnd(HasNext))
        {
            case EndAction.Replay:
                // 循环播放：VLC 在 Ended 之后 Play 不会自己重来，显式归零更可控。
                // 不假装"没播完"——先按看完上报过一次（游标归零），再从头放，语义自洽。
                _endHandled = false;
                Status = "循环播放";
                SeekToMs(0);
                _player?.Play();
                break;

            case EndAction.Next:
                Status = "自动切集…";
                Next();
                break;

            default:
                Status = "已播完";
                break;
        }
    }

    // ── 进度上报（跨端续播）────────────────────────────────────────

    private void MaybeReport(long t)
    {
        if (!_started || _endHandled) return;

        var now = Environment.TickCount64;
        if (now - _lastReportTick < ReportIntervalMs) return;
        // 暂停中位置不动，没必要每 5 秒重复写同一条
        if (Math.Abs(t - _lastReportedMs) < 3_000) return;

        _lastReportTick = now;
        _lastReportedMs = t;
        _ = ReportAsync(t, IsComplete(t));
    }

    /// <summary>离开 / 切条 / 暂停时立刻落一次，不等节流窗口。</summary>
    private void FlushProgress()
    {
        // 已经播完就别再写了：OnEnded 那次 completed=true 才是准的，
        // 这里再补一笔只会把同一个值重复写一遍（自动切集时必然发生）。
        if (!_started || _endHandled || _sourceId.Length == 0) return;
        var pos = _positionMs;
        _lastReportedMs = pos;
        _ = ReportAsync(pos, IsComplete(pos));
    }

    private async Task ReportAsync(long posMs, bool completed)
    {
        var task = ReportCoreAsync(posMs, completed);
        _lastReport = task;
        await task.ConfigureAwait(false);
    }

    private async Task ReportCoreAsync(long posMs, bool completed)
    {
        try
        {
            // completed=true 时服务端会把游标归零，下次就不会从片尾续播
            await _api.ReportProgressAsync(_sourceId, _key, posMs, completed).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // 进度上报失败不该干扰播放：断网了照样能看
            ClientLog.Write($"进度上报失败 {_sourceId}/{_key}@{posMs}：{e.Message}");
        }
    }

    /// <summary>
    /// 看到片尾 20 秒内<b>且</b>走过九成才算看完。
    /// 只看"尾部 20 秒"会把 11 秒的短视频全判成看完（`pos >= duration - 20000` 恒成立）。
    /// </summary>
    private bool IsComplete(long posMs) =>
        _durationMs > 0 &&
        posMs >= _durationMs - CompleteTailMs &&
        posMs * 10 >= _durationMs * 9;

    /// <summary>续播点：与 APK 端 <c>pickResumePosMs</c> 同一判据。</summary>
    private static long PickResumeMs(MediaDetailDto d)
    {
        var pos = d.Meta?.PlayPosMs is > 0 ? d.Meta.PlayPosMs : d.PlayPosMs;
        if (pos <= ResumeMinMs || d.DurationMs <= 0) return 0;
        return pos < d.DurationMs - CompleteTailMs ? pos : 0;
    }

    private void SeekToMs(long ms)
    {
        if (_player is null) return;
        if (_durationMs > 0) ms = Math.Clamp(ms, 0, _durationMs);
        else ms = Math.Max(0, ms);

        _player.Time = ms;
        _positionMs = ms;
        OnPropertyChanged(nameof(PositionMs));
        OnPropertyChanged(nameof(TimeText));
    }

    private static string Fmt(long ms) => MediaCard.FormatDuration(Math.Max(0, ms));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Stop();
        try { _player?.Stop(); } catch { /* 退出路径，忽略 */ }
        _media?.Dispose();
        _media = null;
        _player?.Dispose();

        // LibVLC 是进程级共享实例，不能在这里 Dispose
        _ = _libvlc;
    }

    private sealed record PlayPlan(string Url, string? Slave, string Mode);
}
