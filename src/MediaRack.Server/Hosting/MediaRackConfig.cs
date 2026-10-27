using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaRack.Server.Hosting;

/// <summary>
/// <c>config.json</c> 的磁盘格式。
/// <para>
/// 查找顺序（先命中先用）：
/// <list type="number">
/// <item><c>--config &lt;path&gt;</c> 显式指定</item>
/// <item>exe 同目录的 <c>mediarack.config.json</c> —— 绿色便携部署</item>
/// <item><c>%LOCALAPPDATA%\MediaRack\config.json</c> —— 常规安装</item>
/// </list>
/// 三处都不存在时返回 <see cref="Default"/>（单个 8080 端口、不带密码），
/// 也就是单服务、8080 端口、不设密码，保证老用法不受影响。
/// </para>
/// <para>
/// 这里刻意<b>不</b>复用 <see cref="ServiceProfile"/> 做序列化类型：文件是给人手改的，
/// 必须容忍缺字段、多余字段、注释和尾逗号；运行期模型则要求字段齐全。
/// 两者混用会让"少写一行 name 就整个服务起不来"。
/// </para>
/// </summary>
public sealed record MediaRackConfig
{
    /// <summary>库目录。为空则交给 <see cref="ServerOptions"/> 的默认值或命令行。</summary>
    public string? MetaRoot { get; init; }

    /// <summary>
    /// 公网访问地址（DDNS 域名 / frp 服务端地址 / 端口映射后的地址），仅供管理页展示与二维码使用。
    /// <para>
    /// <b>只能手填</b>：本机看不到路由器的端口映射，也看不到 NAT 出口地址，
    /// 自动猜一个"出口 IP:端口"十有八九是打不通的假配置。
    /// </para>
    /// </summary>
    public string? PublicUrl { get; init; }

    public ServiceConfig[] Services { get; init; } = [];

    public const string FileName = "config.json";
    public const string PortableFileName = "mediarack.config.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // 用户手改时很自然会加注释和尾逗号，不该因此启动失败。
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 不转义中文：否则 D:\视频 会被写成 \u89c6\u9891，手改时没法读。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>没有任何配置文件时的默认：单服务 + 8080 端口 + 不设密码。</summary>
    public static MediaRackConfig Default => new()
    {
        Services = [new ServiceConfig { Name = "全部", Port = 8080 }],
    };

    /// <summary>常规配置路径（无论在不在用，都返回这个位置，供界面显示与保存）。</summary>
    public static string UserPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRack", FileName);

    /// <summary>便携配置路径：exe 所在目录。</summary>
    public static string PortablePath => Path.Combine(AppContext.BaseDirectory, PortableFileName);

    /// <summary>按查找顺序解析出实际使用的文件路径；返回 null 表示一个都不存在。</summary>
    public static string? Locate(string[] args)
    {
        var explicitPath = ArgValue(args, "--config");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);

        if (File.Exists(PortablePath)) return PortablePath;
        if (File.Exists(UserPath)) return UserPath;
        return null;
    }

    /// <summary>
    /// 读取配置。<paramref name="path"/> 为 null 或文件不存在时返回 <see cref="Default"/>；
    /// <b>文件存在但格式错误时抛异常</b>——静默降级到默认值会让人以为"配置没生效"，
    /// 而实际是写错了一个逗号。
    /// </summary>
    public static MediaRackConfig Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Default;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return Default;

        try
        {
            return JsonSerializer.Deserialize<MediaRackConfig>(json, Json) ?? Default;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"配置文件格式错误：{path}（{ex.Message}）", ex);
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>从一段 JSON 解析完整配置对象（测试注入用）。空串返回 <see cref="Default"/>。</summary>
    public static MediaRackConfig FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;

        try
        {
            return JsonSerializer.Deserialize<MediaRackConfig>(json, Json) ?? Default;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"服务配置 JSON 不合法：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 从一段 <b>只含服务数组</b> 的 JSON 解析（<c>MediaRack:Services</c> 这个键用的就是这种）。
    /// 空串返回 <see cref="Default"/>。
    /// </summary>
    public static MediaRackConfig FromServicesJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;

        try
        {
            var services = JsonSerializer.Deserialize<ServiceConfig[]>(json, Json);
            return services is null ? Default : new MediaRackConfig { Services = services };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"services JSON 不合法：{ex.Message}", ex);
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        foreach (var arg in args)
        {
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return arg[(name.Length + 1)..];
        }

        return null;
    }
}

/// <summary>文件里的一个服务条目。字段全是"可缺省"，缺省值见各属性。</summary>
public sealed record ServiceConfig
{
    /// <summary>缺省时显示为"服务 8080"。</summary>
    public string? Name { get; init; }

    /// <summary>0 表示未配置，会被判为非法（端口必须显式给）。</summary>
    public int Port { get; init; }

    /// <summary>null / 空串 = 完全公开。</summary>
    public string? Password { get; init; }

    /// <summary>缺省 true（可写）。老配置里不写这个字段也保持可写。</summary>
    public bool Writable { get; init; } = true;

    /// <summary>空数组 = 可见全部源。</summary>
    public string[]? Sources { get; init; }
}
