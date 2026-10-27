using System.Collections.ObjectModel;
using System.Windows.Input;
using MediaRack.Client.Remote;
using MediaRack.Client.Storage;

namespace MediaRack.Client.ViewModels;

/// <summary>
/// 连接页：手输地址 / 粘贴配对码 → 探活 → 验令牌 → 拉源列表。
///
/// 这是客户端的第一个界面，也是「别的 PC 装上就能连」这条要求的落点：
/// 一条 base URL + 一个令牌 = 一台服务端，和 APK 添加服务器的形态一致。
/// </summary>
public sealed class ConnectViewModel : ViewModelBase
{
    private readonly Action<ServerEntry, List<SourceInfoDto>, ApiClient> _onConnected;
    private string _input = "";
    private string _token = "";
    private string _status = "";
    private bool _isError;

    /// <param name="notice">进入连接页时要显示的一句话（比如"上次的地址连不上了"）。</param>
    public ConnectViewModel(Action<ServerEntry, List<SourceInfoDto>, ApiClient> onConnected,
        string? notice = null, bool isError = false)
    {
        _onConnected = onConnected;
        _status = notice ?? "";
        _isError = isError;
        Saved = new ObservableCollection<ServerEntry>(ConnectionStore.Load());
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => Input.Trim().Length > 0);
        RemoveSavedCommand = new RelayCommand<ServerEntry>(RemoveSaved);
        ConnectSavedCommand = new RelayCommand<ServerEntry>(UseSaved);
    }

    public ObservableCollection<ServerEntry> Saved { get; }

    /// <summary>地址，或整段配对码内容（<c>MR1|...</c> / <c>http://...#token=xxx</c>）。</summary>
    public string Input
    {
        get => _input;
        set
        {
            if (Set(ref _input, value)) Status = "";
        }
    }

    /// <summary>令牌。配对码自带时会自动填进来，手输地址时可留空（服务端没设密码时）。</summary>
    public string Token
    {
        get => _token;
        set => Set(ref _token, value);
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

    public AsyncRelayCommand ConnectCommand { get; }

    public ICommand RemoveSavedCommand { get; }

    public ICommand ConnectSavedCommand { get; }

    /// <summary>点已保存的条目直接连它。</summary>
    public void UseSaved(ServerEntry entry)
    {
        Input = entry.BaseUrl;
        Token = entry.Token ?? "";
        ConnectCommand.Execute(null);
    }

    public void RemoveSaved(ServerEntry entry)
    {
        ConnectionStore.Remove(entry.Id);
        Saved.Remove(entry);
    }

    private async Task ConnectAsync()
    {
        IsError = false;
        Status = "正在解析…";

        var raw = Input.Trim();

        // 1. 解析：配对码优先（一张码打包多候选地址 + 令牌）
        string? token = string.IsNullOrWhiteSpace(Token) ? null : Token.Trim();
        List<string> candidates;

        var pairing = PairingCode.Parse(raw);
        if (pairing is not null)
        {
            candidates = pairing.Urls;
            if (!string.IsNullOrEmpty(pairing.Token)) token = pairing.Token;
            Token = token ?? "";
        }
        else
        {
            var single = ApiClient.NormalizeBaseUrl(raw);
            if (single is null)
            {
                Fail("地址格式不对。可以是 http://host:port、/s/源门户地址，或整段配对码内容");
                return;
            }
            candidates = new List<string> { single };
        }

        // 2. 探活：多候选并发探，按服务端给的优先级（Tailscale 在前）取第一个可达的
        string baseUrl;
        if (candidates.Count == 1)
        {
            baseUrl = candidates[0];
            Status = $"正在连接 {baseUrl}…";
        }
        else
        {
            Status = $"正在从 {candidates.Count} 个候选地址中挑一个可达的…";
            var picked = await PairingCode.PickReachableBaseUrlAsync(candidates, token).ConfigureAwait(true);
            if (picked is null)
            {
                Fail($"{candidates.Count} 个候选地址都连不上（域名解析失败 / 连接被拒绝 / 超时）");
                return;
            }
            baseUrl = picked;
            Status = $"已选 {baseUrl}，正在验证…";
        }

        var api = new ApiClient(baseUrl, token);

        // 3. 验令牌：带 Bearer 打 auth/state，错了这里就知道，不用等到播放 401
        try
        {
            var auth = await api.AuthStateAsync().ConfigureAwait(true);
            if (auth.AuthRequired && !auth.Authenticated)
            {
                api.Dispose();
                Fail(token is null
                    ? "这台服务端要求令牌，请填写后再连接"
                    : "令牌无效或已失效，请重新扫码获取");
                return;
            }
        }
        catch (NetworkException e)
        {
            api.Dispose();
            Fail($"连不上：{e.Message}");
            return;
        }
        catch (ApiException e)
        {
            api.Dispose();
            Fail(e.Message);
            return;
        }

        // 4. 拉源列表——连上就得看见东西，这一步成功才算连接成立
        List<SourceInfoDto> sources;
        HealthDto? health = null;
        try
        {
            try { health = await api.HealthAsync().ConfigureAwait(true); } catch { /* 非必需 */ }
            sources = await api.SourcesAsync().ConfigureAwait(true);
        }
        catch (NetworkException e)
        {
            api.Dispose();
            Fail($"连不上：{e.Message}");
            return;
        }
        catch (ApiException e)
        {
            api.Dispose();
            Fail(e.Message);
            return;
        }

        var name = health?.Version is { Length: > 0 } v
            ? $"{baseUrl}（v{v}）"
            : baseUrl;

        var entry = ConnectionStore.Upsert(baseUrl, token, name);
        // api 交给 Shell 持有（后面浏览/播放都用它），这里不能 dispose
        _onConnected(entry, sources, api);
    }

    private void Fail(string message)
    {
        Status = message;
        IsError = true;
    }
}
