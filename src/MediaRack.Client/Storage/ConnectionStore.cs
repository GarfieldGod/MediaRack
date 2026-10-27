using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaRack.Client.Storage;

/// <summary>一条已保存的服务端连接。与 Android 端 <c>core/db/ServerEntity</c> 同一概念。</summary>
public sealed class ServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>展示名：连上后用服务端版本/源名回填，手输时就是地址本身。</summary>
    public string Name { get; set; } = "";
    /// <summary>base URL（可含路径前缀，门户形态是 <c>http://host:port/s/{slug}</c>）。</summary>
    public string BaseUrl { get; set; } = "";
    public string? Token { get; set; }
    public long LastUsedAt { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? BaseUrl : Name;
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

    public static string DirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRack");

    public static string FilePath => Path.Combine(DirPath, "client-servers.json");

    public static List<ServerEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<ServerEntry>();
            var text = File.ReadAllText(FilePath);
            var shape = JsonSerializer.Deserialize<FileShape>(text, Json);
            return shape?.Servers ?? new List<ServerEntry>();
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

    /// <summary>新增或按 BaseUrl 更新，返回落库后的条目。</summary>
    public static ServerEntry Upsert(string baseUrl, string? token, string? name = null)
    {
        var all = Load();
        var hit = all.FirstOrDefault(s => string.Equals(s.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            hit = new ServerEntry { BaseUrl = baseUrl };
            all.Add(hit);
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
