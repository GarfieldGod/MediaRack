using System.Collections.ObjectModel;
using MediaRack.Client.Remote;
using MediaRack.Client.Storage;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 连接页：<b>一个源地址框 + 令牌 + 名称</b>。把管理页「访问方式」里那一整行粘进来就行。
///
/// **为什么是一个框**：服务端管理页给出的最小单位本来就是一条完整地址
/// （<c>http://主机:端口/s/源标识</c>，每行带一个「复制」按钮），配对二维码里也是这一串。
/// 拆成"服务器地址 / 端口 / 源地址"三栏，等于让用户把已经是一整行的东西自己拆开再填三遍。
/// 桌面主路径是复制粘贴，所以合并；手机软键盘分栏各有键盘类型、主路径是扫码，那边仍是四个必填栏。
///
/// **令牌不能拼进地址**：它会跟着 URL 进日志、进历史。所以令牌永远是一栏；
/// 粘进来的串里带 <c>#token=</c> 时，只在输入框里把那一段剥出来填进令牌栏，框里只留地址。
///
/// 一个框吃三种输入：完整地址 / 省了 scheme 的地址（<c>192.0.2.10:8080/s/movie</c>）/
/// 配对码 <c>MR1|{"urls":[…]}</c>（多候选地址，点连接时并发探活挑一个能连上的）。
///
/// 主页可以同时连着多个源，所以这里连上**不会**顶掉别的源——它只是往会话池里加一个。
/// </summary>
public sealed class ConnectViewModel : ViewModelBase
{
    private const string PairingPrefix = "MR1|";
    private const string TokenHash = "#token=";

    private readonly Action<ServerEntry, List<SourceInfoDto>, ApiClient, string?> _onConnected;
    private readonly Func<string, bool> _isConnected;
    private string _sourceUrl = "";
    private string _token = "";
    private string _name = "";
    private string _status = "";
    private bool _isError;
    private ServerEntry? _editing;

    /// <param name="onConnected">连上后回调（携带服务端返回的源列表与版本）。</param>
    /// <param name="notice">进入页面要显示的一句话（比如"上次的源连不上了"）。</param>
    /// <param name="isConnected">某条已保存配置是不是正连着（列表里标「已连接」）。</param>
    /// <param name="editing">
    /// 非空 = 从主页左栏某个源的「改」进来的：源地址预填成它的值，连接成功后由外壳替换掉那个会话。
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

    // ── 三个字段 ───────────────────────────────────────────────────

    /// <summary>
    /// 源地址：<b>一整行</b>，<c>http://主机:端口/s/源标识</c>（scheme 可省）。
    /// 粘进来的串带 <c>#token=xxx</c> 时，那一段被剥出来填进 <see cref="Token"/>，框里只留地址。
    /// </summary>
    public string SourceUrl
    {
        get => _sourceUrl;
        set
        {
            var text = value ?? "";

            // 管理页二维码 / 带令牌的分享链接是 http://…/s/slug#token=xxx：一个框粘完就该全填好，
            // 所以令牌先剥出来，框里只留地址（令牌留在框里会跟着 URL 进日志）。
            var hash = text.IndexOf(TokenHash, StringComparison.Ordinal);
            if (hash >= 0)
            {
                var token = text[(hash + TokenHash.Length)..].Trim();
                text = text[..hash];
                if (token.Length > 0) Set(ref _token, token, nameof(Token));
            }

            if (Set(ref _sourceUrl, text.Trim())) RefreshParsed();
        }
    }

    /// <summary>访问令牌。公开源可以留空。</summary>
    public string Token
    {
        get => _token;
        set => Set(ref _token, value);
    }

    /// <summary>展示名，可留空——留空就用服务端里这个源自己的名字。</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    // ── 实时解析回显 ───────────────────────────────────────────────

    /// <summary>
    /// 一行回显：解析成功说清"要连哪儿"，失败说清"差什么"。
    /// 单框必须配这个——否则端口写错和源标识写错在界面上长得一模一样。
    /// </summary>
    public string AddressPreview
    {
        get
        {
            if (SourceUrl.Trim().Length == 0)
                return "把管理页「访问方式」里那一整行粘进来，例如 http://192.0.2.10:8080/movie";

            if (IsPairingCode)
                return "配对码：里面可能有好几个候选地址，点「连接」时自动挑一个能连上的";

            var p = Parse();
            if (p.Error is { } error) return "⚠ " + error;
            return p.Note is { Length: > 0 } note ? $"将连接 {p.BaseUrl} · {note}" : $"将连接 {p.BaseUrl}";
        }
    }

    /// <summary>回显是不是一条错误（决定它显示成红色还是灰色）。空输入不算错。</summary>
    public bool IsAddressProblem =>
        SourceUrl.Trim().Length > 0 && !IsPairingCode && !Parse().Ok;

    /// <summary>解析出来的完整门户地址；解析不出就是 null。</summary>
    public string? ResolvedUrl => IsPairingCode ? null : Parse().Ok ? Parse().BaseUrl : null;

    /// <summary>
    /// 能不能点连接。**源标识必须在**：只有 <c>http://主机:端口</c> 的话只能连服务端根路径，
    /// 而根路径是管理页专用通道（一次列出全部源），客户端一律不认，不做降级。
    /// </summary>
    public bool CanConnect => IsPairingCode || Parse().Ok;

    private bool IsPairingCode => SourceUrl.Trim().StartsWith(PairingPrefix, StringComparison.Ordinal);

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
    public RelayCommand CancelEditCommand { get; }

    /// <summary>关掉覆盖层（右上角 ✕ / 取消）。没有外壳时（自检）就是个空命令。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>
    /// 字段一变就重算按钮状态。**这是"连接按钮点不动"的老根因**：命令的 CanExecute 依赖输入内容，
    /// 而 Avalonia 只在 CanExecuteChanged 时重查 IsEnabled——不通知的话按钮会永远停在
    /// 初始的"空输入 = 禁用"上。
    /// </summary>
    private void RefreshParsed()
    {
        if (Status.Length > 0 && !IsError) Status = "";
        ConnectCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(AddressPreview));
        OnPropertyChanged(nameof(IsAddressProblem));
        OnPropertyChanged(nameof(ResolvedUrl));
    }

    // ── 地址解析 ───────────────────────────────────────────────────

    /// <summary>一条源地址拆开的结果。<see cref="BaseUrl"/> 只对解析成功的有意义。</summary>
    private readonly record struct AddressParse(
        string Scheme, string Authority, string Host, string Port, string Slug, string? Error, string? Note)
    {
        public bool Ok => Error is null;

        public string BaseUrl => $"{Scheme}://{Authority}/s/{Slug}";

        public static AddressParse Fail(string error) => new("", "", "", "", "", error, null);
    }

    /// <summary>
    /// 把用户粘进来的那一行拆成 (scheme, authority, 端口, 源标识)。
    /// 容错按实际会遇到的输法来定：省 scheme、省端口、尾斜杠、漏了 <c>/s/</c> 前缀、
    /// 带 <c>/s/slug/</c> 后面的多余路径——都按"他就是想连这个源"处理，不拿格式卡人。
    /// </summary>
    private AddressParse Parse()
    {
        var raw = SourceUrl.Trim();
        if (raw.Length == 0) return AddressParse.Fail("还没填地址");
        if (raw.StartsWith(PairingPrefix, StringComparison.Ordinal))
            return AddressParse.Fail("这是配对码，点「连接」会自己挑地址");

        // 手打最常见的形态是 192.0.2.10:8080/movie，没有 scheme；Uri 不认，补一个 http://
        var text = raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return AddressParse.Fail("这段不是地址，要形如 http://主机:端口/源标识");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return AddressParse.Fail($"只支持 http / https，这条是 {uri.Scheme}");

        // 源标识：/s/SB → SB；漏了 /s/ 的 /SB 也算 SB（人就是会漏）；/s/SB/xxx 只要第一段
        var path = uri.AbsolutePath.Trim('/');
        if (path.StartsWith("s/", StringComparison.Ordinal)) path = path[2..];
        var slug = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (slug.Length == 0)
            return AddressParse.Fail("这条地址只有服务器，缺源标识（形如 http://主机:端口/SB）——" +
                                     "没有它就只能连服务端根，而根是管理页通道，客户端不认");

        // 没写端口不拦，但要说明白：80 和服务端默认的 8080 不是一回事，不说清就是"莫名其妙连不上"
        var note = uri.IsDefaultPort ? $"没写端口，按 {uri.Port} 算（MediaRack 默认 8080）" : null;

        return new AddressParse(uri.Scheme, uri.Authority, uri.Host, uri.Port.ToString(), slug, null, note);
    }

    // ── 已保存列表 ─────────────────────────────────────────────────

    private void ReloadSaved()
    {
        Saved.Clear();
        foreach (var e in ConnectionStore.Load())
            Saved.Add(new SavedSource(e, _isConnected(e.BaseUrl), UseSaved, BeginEdit, RemoveSaved));
    }

    /// <summary>点已保存的行：直接连它（地址与令牌一起回填，连接按钮此时一定是可用的）。</summary>
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

    /// <summary>把一条 base URL 拆回三个字段（编辑、点已保存行都走这里）。</summary>
    private void FillFrom(string baseUrl, string? token, string? name)
    {
        SourceUrl = baseUrl;
        Token = token ?? "";
        if (!string.IsNullOrWhiteSpace(name)) Name = name!;
    }

    // ── 连接 ───────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        IsError = false;

        string? baseUrl;
        string? token = string.IsNullOrWhiteSpace(Token) ? null : Token.Trim();

        if (IsPairingCode)
        {
            // 配对码：一张码可能打包了 Tailscale / 内网 / 公网几个候选地址，并发探活挑第一个能连上的
            var payload = PairingCode.Parse(SourceUrl);
            if (payload is null)
            {
                Fail("这个配对码解析不出来，重新生成一张或直接粘地址");
                return;
            }

            token ??= payload.Token;

            string? picked;
            if (payload.Urls.Count == 1)
            {
                picked = payload.Urls[0];
            }
            else
            {
                Status = $"正在从 {payload.Urls.Count} 个候选地址里挑一个可达的…";
                picked = await PairingCode.PickReachableBaseUrlAsync(payload.Urls, token).ConfigureAwait(true);
            }

            if (picked is null)
            {
                Fail($"{(payload.Urls.Count == 1 ? "这个地址" : $"{payload.Urls.Count} 个候选地址都")}连不上");
                return;
            }

            if (!ApiClient.IsSourcePortalUrl(picked))
            {
                // 码里没有源标识就不是源门户的码。不降级连根：根的源列表是管理页通道。
                Fail("这个码里没有源地址。请到管理页重新生成该源的二维码，或直接粘「访问方式」里那一行");
                return;
            }

            // 把挑中的地址与令牌落回可见的字段，别让人不知道到底连了哪个
            SourceUrl = picked;
            Token = token ?? "";
            baseUrl = picked;
        }
        else
        {
            var parsed = Parse();
            if (!parsed.Ok)
            {
                Fail(parsed.Error!);
                return;
            }

            // 归一化后的门户地址：/s/ 前缀由这里统一加，用户漏了也补上
            baseUrl = ApiClient.NormalizeBaseUrl(parsed.BaseUrl);
            if (baseUrl is null)
            {
                Fail("地址拼不出来，检查一下主机名和端口");
                return;
            }
        }

        // 两个分支都给非空值；这条只是给编译器一个交代，走到这儿说明逻辑写漏了
        if (baseUrl is null)
        {
            Fail("地址拼不出来，检查一下主机名和端口");
            return;
        }

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
                Fail($"这个地址返回了 {sources.Count} 个源，不是源门户地址（应形如 http://主机:端口/SB）");
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
