using System.Collections.ObjectModel;
using MediaRack.Client.Remote;
using MediaRack.Client.Storage;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 连接页：填四栏（服务器地址 / 端口 / 源地址 / 令牌）+ 可选的粘贴配对码，连上就入库。
///
/// **一条配置 = 一个源**，与 Android 的「添加源」四栏逐字段对应（地址、端口、源地址、
/// 访问令牌就是服务端管理页「访问方式」里的四个字段）。桌面没有摄像头，扫码换成
/// 「粘贴二维码内容」：配对码里的候选地址会并发探活，挑一个能连上的回填。
///
/// 主页可以同时连着多个源，所以这里连上**不会**顶掉别的源——它只是往会话池里加一个。
/// </summary>
public sealed class ConnectViewModel : ViewModelBase
{
    private readonly Action<ServerEntry, List<SourceInfoDto>, ApiClient, string?> _onConnected;
    private readonly Func<string, bool> _isConnected;
    private string _host = "";
    private string _port = "";
    private string _sourcePath = "";
    private string _token = "";
    private string _name = "";
    private string _paste = "";
    private string _status = "";
    private bool _isError;
    private ServerEntry? _editing;

    /// <param name="onConnected">连上后回调（携带服务端返回的源列表与版本）。</param>
    /// <param name="notice">进入页面要显示的一句话（比如"上次的源连不上了"）。</param>
    /// <param name="isConnected">某条已保存配置是不是正连着（列表里标「已连接」）。</param>
    /// <param name="editing">
    /// 非空 = 从主页左栏某个源的「改」进来的：四栏预填成它的值，连接成功后由外壳替换掉那个会话。
    /// 与"点已保存行上的编辑"共用同一套 <see cref="IsEditing"/> 状态。
    /// </param>
    /// <param name="onCancel">
    /// 关闭这个覆盖层（取消 / 右上角 ✕）。连接页现在是盖在海报墙上的弹层，
    /// 所以必须有个"什么都不做、退回去"的出口——手机上就是返回手势。
    /// </param>
    public ConnectViewModel(Action<ServerEntry, List<SourceInfoDto>, ApiClient, string?> onConnected,
        string? notice = null, bool isError = false, Func<string, bool>? isConnected = null,
        ServerEntry? editing = null, Action? onCancel = null)
    {
        _onConnected = onConnected;
        _isConnected = isConnected ?? (_ => false);
        _status = notice ?? "";
        _isError = isError;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanConnect);
        PasteCommand = new AsyncRelayCommand(ApplyPasteAsync, () => Paste.Trim().Length > 0);
        CancelEditCommand = new RelayCommand(ClearEdit, () => _editing is not null);
        CloseCommand = new RelayCommand(() => onCancel?.Invoke(), () => onCancel is not null);

        ReloadSaved();

        if (editing is not null)
        {
            _editing = editing;
            FillFrom(editing.BaseUrl, editing.Token, editing.Name);
            Status = "改完点「连接」即替换这个源（地址改过的话旧连接会被断开）";
            IsError = false;
        }
    }

    public ObservableCollection<SavedSource> Saved { get; } = new();

    // ── 四栏 + 名称 ────────────────────────────────────────────────

    /// <summary>服务器地址：IP 或主机名，<b>不带</b> scheme 与端口。</summary>
    public string Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetField(ref _port, value);
    }

    /// <summary>源地址（slug）：管理页「访问方式」里的源标识，拼成 <c>/s/{slug}</c>。</summary>
    public string SourcePath
    {
        get => _sourcePath;
        set => SetField(ref _sourcePath, value);
    }

    public string Token
    {
        get => _token;
        set => SetField(ref _token, value);
    }

    /// <summary>展示名，可留空——留空就用服务端里这个源自己的名字。</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>整段配对码 / 带令牌的链接，粘进来一键回填四栏。</summary>
    public string Paste
    {
        get => _paste;
        set
        {
            if (Set(ref _paste, value)) PasteCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>四栏拼出来的地址，让人在点连接之前先看清"我要连的是哪儿"。</summary>
    public string AddressPreview =>
        BuildBaseUrlFromFields() ?? "（服务器地址、端口、源地址都填上后，这里显示完整地址）";

    /// <summary>
    /// 三栏都是硬性的。<b>源地址不许留空</b>：留空只能连服务端根路径，
    /// 而根路径是管理页专用通道（服务端按 loopback 放行），客户端拿它就能一次列出全部源。
    /// 一条配置只能对应一个源，所以这里没有"留空 = 连服务器"这条分支。
    /// </summary>
    public bool CanConnect =>
        Host.Trim().Length > 0 && Port.Trim().Length > 0 && Slug.Length > 0;

    /// <summary>源地址栏归一化后的 slug（剥掉可能被粘进来的 <c>/s/</c> 前缀与首尾斜杠）。</summary>
    private string Slug
    {
        get
        {
            var slug = SourcePath.Trim().Trim('/');
            if (slug.StartsWith("s/", StringComparison.Ordinal)) slug = slug[2..];
            return slug.Trim('/');
        }
    }

    /// <summary>四栏 → base URL。任何一栏缺了都返回 null（不回落根路径）。</summary>
    private string? BuildBaseUrlFromFields()
    {
        var host = Host.Trim();
        var port = Port.Trim();
        var slug = Slug;
        if (host.Length == 0 || port.Length == 0 || slug.Length == 0) return null;
        return ApiClient.BuildSourceBaseUrl(host, port, slug);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool IsError
    {
        get => _isError;
        set => Set(ref _isError, value);
    }

    /// <summary>正在编辑某条已保存配置（改完点连接即覆盖它，不会多出一条）。</summary>
    public bool IsEditing => _editing is not null;

    public string EditHint => _editing is null ? "" : $"编辑中：{_editing.DisplayName}";

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand PasteCommand { get; }
    public RelayCommand CancelEditCommand { get; }

    /// <summary>关掉覆盖层（右上角 ✕ / 取消）。没有外壳时（自检）就是个空命令。</summary>
    public RelayCommand CloseCommand { get; }

    // ── 字段变更 ───────────────────────────────────────────────────

    /// <summary>
    /// 四栏任一变化都要重算按钮状态。**这是连接按钮点不动的根因所在**：
    /// 命令的 CanExecute 依赖输入内容，而 Avalonia 只在 CanExecuteChanged 时重查 IsEnabled——
    /// 不通知的话按钮会永远停在初始的"空输入 = 禁用"上。
    /// </summary>
    private void SetField(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return;
        if (Status.Length > 0 && !IsError) Status = "";
        ConnectCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(AddressPreview));
    }

    // ── 已保存列表 ─────────────────────────────────────────────────

    private void ReloadSaved()
    {
        Saved.Clear();
        foreach (var e in ConnectionStore.Load())
            Saved.Add(new SavedSource(e, _isConnected(e.BaseUrl), UseSaved, BeginEdit, RemoveSaved));
    }

    /// <summary>点已保存的行：直接连它（四栏一起回填，连接按钮此时一定是可用的）。</summary>
    private void UseSaved(SavedSource row)
    {
        FillFrom(row.Entry.BaseUrl, row.Entry.Token, row.Entry.Name);
        _editing = null;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditHint));
        ConnectCommand.Execute(null);
    }

    private void BeginEdit(SavedSource row)
    {
        _editing = row.Entry;
        FillFrom(row.Entry.BaseUrl, row.Entry.Token, row.Entry.Name);
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditHint));
        CancelEditCommand.RaiseCanExecuteChanged();
        Status = "改完点「连接」即保存到这一条（地址变了也不会多出一条）";
        IsError = false;
    }

    private void ClearEdit()
    {
        _editing = null;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditHint));
        CancelEditCommand.RaiseCanExecuteChanged();
        Status = "";
    }

    private void RemoveSaved(SavedSource row)
    {
        ConnectionStore.Remove(row.Entry.Id);
        Saved.Remove(row);
        if (_editing is not null && _editing.Id == row.Entry.Id) ClearEdit();
        Status = $"已删除「{row.Title}」的配置（正在连接的会话不受影响）";
        IsError = false;
    }

    // ── 粘贴配对码 ─────────────────────────────────────────────────

    private async Task ApplyPasteAsync()
    {
        IsError = false;
        var payload = PairingCode.Parse(Paste);
        if (payload is null)
        {
            Fail("这段内容既不是配对码也不是地址");
            return;
        }

        if (payload.Token is { Length: > 0 }) Token = payload.Token;

        string? picked;
        if (payload.Urls.Count == 1)
        {
            picked = payload.Urls[0];
        }
        else
        {
            Status = $"正在从 {payload.Urls.Count} 个候选地址里挑一个可达的…";
            picked = await PairingCode.PickReachableBaseUrlAsync(payload.Urls, payload.Token).ConfigureAwait(true);
        }

        if (picked is null)
        {
            Fail($"{(payload.Urls.Count == 1 ? "这个地址" : $"{payload.Urls.Count} 个候选地址都")}连不上");
            return;
        }

        FillFrom(picked, payload.Token, null);
        if (Slug.Length == 0)
        {
            // 码里没有源地址就不是源门户的码（源门户的码一定带 /s/{slug}）。
            // 不降级连根：根的源列表是管理页通道，客户端不该走。
            Fail("这个码里没有源地址。请到管理页重新生成该源的二维码，或手填源地址");
            return;
        }

        IsError = false;
        Status = "已按配对码回填，检查无误后点「连接」";
        ConnectCommand.RaiseCanExecuteChanged();
    }

    /// <summary>把一条 base URL 拆回四栏（编辑、点已保存行、粘到码都走这里）。</summary>
    private void FillFrom(string baseUrl, string? token, string? name)
    {
        var (host, port, path) = ApiClient.SplitSourceBaseUrl(baseUrl);

        // 源地址栏只放 slug：URL 里的 /s/ 前缀是拼地址时才加的，回填时剥掉，
        // 否则再拼一次就变成 /s/s/movie。
        var slug = path.TrimStart('/');
        if (slug.StartsWith("s/", StringComparison.Ordinal)) slug = slug[2..];

        Host = host;
        Port = port;
        SourcePath = slug;
        Token = token ?? "";
        if (!string.IsNullOrWhiteSpace(name)) Name = name!;
    }

    // ── 连接 ───────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        IsError = false;

        var baseUrl = BuildBaseUrlFromFields();
        if (baseUrl is null)
        {
            Fail(Slug.Length == 0
                ? "源地址不能留空：一条配置只连一个源，地址形如 http://主机:端口/s/{源标识}"
                : "服务器地址和端口都要填");
            return;
        }

        var token = string.IsNullOrWhiteSpace(Token) ? null : Token.Trim();
        Status = $"正在连接 {baseUrl}…";

        var api = new ApiClient(baseUrl, token);
        try
        {
            // ① 验令牌：错了现在就知道，不用等到播放 401
            var auth = await api.AuthStateAsync().ConfigureAwait(true);
            if (auth.AuthRequired && !auth.Authenticated)
            {
                api.Dispose();
                Fail(token is null ? "这个源要求令牌，请填「访问令牌」" : "令牌无效或已失效，请重新扫码或到管理页确认");
                return;
            }

            // ② 拉源列表：连上就得看见东西，这一步成功才算连接成立
            string? version = null;
            try { version = (await api.HealthAsync().ConfigureAwait(true)).Version; } catch { /* 非必需 */ }
            var sources = await api.SourcesAsync().ConfigureAwait(true);

            // 源门户下必然只有一条。多于一条说明这个地址不是源门户（比如退回了根路径），
            // 而「一次拿到多个源」是客户端绝对不接受的形态——宁可拒连，不猜它想连哪个。
            if (sources.Count != 1)
            {
                api.Dispose();
                Fail($"这个地址返回了 {sources.Count} 个源，不是源门户地址（应形如 http://主机:端口/s/{{源标识}}）");
                return;
            }

            // 名称留空就用服务端里这个源的名字（与 APK 一致）
            var name = string.IsNullOrWhiteSpace(Name) ? sources[0].Name : Name.Trim();
            var entry = ConnectionStore.Upsert(baseUrl, token, name, _editing?.Id);

            // api 交给会话池持有（后面浏览/播放都用它），这里不能 dispose
            Tools.ClientLog.Write($"连接页连上：{baseUrl}（{sources.Count} 个源，名称「{name}」）");
            _onConnected(entry, sources, api, version);
        }
        catch (NetworkException e)
        {
            api.Dispose();
            Fail($"连不上：{e.Message}");
        }
        catch (ApiException e)
        {
            api.Dispose();
            Fail(e.Message);
        }
        catch (Exception e)
        {
            // 兜底：任何没预料到的异常都要变成屏幕上一句话，而不是让「正在连接…」永远停在那
            api.Dispose();
            Fail($"连接失败：{e.Message}");
            Tools.ClientLog.Write($"[连接页] {baseUrl} → {e}");
        }
    }

    private void Fail(string message)
    {
        Status = message;
        IsError = true;
    }
}
