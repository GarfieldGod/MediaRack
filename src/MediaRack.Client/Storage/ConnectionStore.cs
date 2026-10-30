using System.Text.Json;
using System.Text.Json.Serialization;
using MediaRack.Client.Remote;

namespace MediaRack.Client.Storage;

/// <summary>
/// 一条已保存的连接配置 = <b>一个源</b>（源门户 <c>http://host:port/s/{slug}</c>）。
/// 落盘只有 base URL + 令牌两条：PC 端用一个完整地址框输入，<see cref="ApiClient.SplitSourceBaseUrl"/>
/// 只负责把它拆回 host / port / path 供列表显示，不参与存储。
/// <para>
/// 与 Android 端 <c>core/db/ServerEntity</c> 的对应关系是"同一个源"，不是"同一堆字段"：
/// 那边按字段存（地址、名称各自一栏），这边一条 base URL 就够，老配置照样能读。
/// </para>
/// </summary>
public sealed class ServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>展示名：留空时连上后用服务端里的源名回填。</summary>
    public string Name { get; set; } = "";
    /// <summary>base URL（可含路径前缀，门户形态是 <c>http://host:port/s/{slug}</c>）。</summary>
    public string BaseUrl { get; set; } = "";
    public string? Token { get; set; }
    public long LastUsedAt { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? BaseUrl : Name;

    /// <summary>base URL 的拆解结果（列表显示用，不参与存储）。</summary>
    [JsonIgnore]
    public (string Host, string Port, string SourcePath) Fields => ApiClient.SplitSourceBaseUrl(BaseUrl);

    /// <summary><c>host:port/s/slug</c>：列表里的第二行，不含 scheme。</summary>
    [JsonIgnore]
    public string AddressLine
    {
        get
        {
            var (host, port, path) = Fields;
            if (host.Length == 0) return BaseUrl;
            return port.Length == 0 ? $"{host}{path}" : $"{host}:{port}{path}";
        }
    }
}

/// <summary>
/// 连接配置持久化。
///
/// 位置：<see cref="Environment.SpecialFolder.LocalApplicationData"/>/MediaRack/client-servers.json
/// （Windows=%LOCALAPPDATA%，macOS=~/Library/Application Support，Linux=~/.local/share）。
/// 与服务端 config.json 同目录不同文件，互不干扰。
/// </summary>
public static class ConnectionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class FileShape
    {
        public int Version { get; set; } = 1;
        public List<ServerEntry> Servers { get; set; } = new();
    }

    /// <summary>
    /// 配置目录。<c>MEDIARACK_HOME</c> 可把它指到别处——无头自检用临时目录，
    /// 免得跑一次 <c>--connecttest</c> 就往用户真实配置里塞一条测试源。
    /// </summary>
    public static string DirPath =>
        Environment.GetEnvironmentVariable("MEDIARACK_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRack");

    public static string FilePath => Path.Combine(DirPath, "client-servers.json");

    public static List<ServerEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<ServerEntry>();
            var text = File.ReadAllText(FilePath);
            var shape = JsonSerializer.Deserialize<FileShape>(text, Json);
            var all = shape?.Servers ?? new List<ServerEntry>();

            // 只有服务器地址、没有 /s/{slug} 的配置属于旧版（那时客户端还能连服务端根）。
            // 根是管理页通道、一次列出全部源，客户端已经一律不认——所以这类条目
            // 既连不上也没有"补一格就能救"以外的用法，直接丢掉并回写文件。
            // 留着它只会在界面上多一行红字，等于把历史包袱摆给用户看。
            var kept = all.Where(s => ApiClient.IsSourcePortalUrl(s.BaseUrl)).ToList();
            if (kept.Count != all.Count)
            {
                Tools.ClientLog.Write($"丢弃 {all.Count - kept.Count} 条旧式配置（只有服务器地址、缺源地址）");
                Save(kept);
            }

            return kept;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置坏了当没有配置——不能因为它开不了窗
            return new List<ServerEntry>();
        }
    }

    public static void Save(IEnumerable<ServerEntry> servers)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            var shape = new FileShape { Servers = servers.OrderByDescending(s => s.LastUsedAt).ToList() };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(shape, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 写不了配置不该导致连不上：本次会话内仍然可用
        }
    }

    /// <summary>
    /// 新增或更新，返回落库后的条目。
    ///
    /// 定点更新用 <paramref name="replaceId"/>（编辑一条已保存配置、可能连地址一起改）：
    /// 命中就改它，不命中再按 base URL 认领；同一 base URL 的其它重复行顺手清掉，
    /// 免得列表里出现两行一模一样的源。
    /// </summary>
    public static ServerEntry Upsert(string baseUrl, string? token, string? name = null, string? replaceId = null)
    {
        var all = Load();
        var hit = replaceId is { Length: > 0 } ? all.FirstOrDefault(s => s.Id == replaceId) : null;
        hit ??= all.FirstOrDefault(s => string.Equals(s.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase));

        if (hit is null)
        {
            hit = new ServerEntry { BaseUrl = baseUrl };
            all.Add(hit);
        }
        else
        {
            hit.BaseUrl = baseUrl;
            all.RemoveAll(s => !ReferenceEquals(s, hit) &&
                               string.Equals(s.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase));
        }

        hit.Token = string.IsNullOrWhiteSpace(token) ? null : token;
        if (!string.IsNullOrWhiteSpace(name)) hit.Name = name!;
        hit.LastUsedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Save(all);
        return hit;
    }

    public static void Remove(string id)
    {
        var all = Load();
        Save(all.Where(s => s.Id != id));
    }
}
