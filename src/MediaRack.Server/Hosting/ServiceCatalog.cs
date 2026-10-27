namespace MediaRack.Server.Hosting;

/// <summary>
/// 本次进程要对外提供的全部服务。启动时解析一次，之后只读。
/// <para>
/// 两种来源：
/// <list type="bullet">
/// <item><b>显式配置</b>（有 config.json）—— 用它列的端口逐个监听，是"多服务"模式。</item>
/// <item><b>传统模式</b>（没有 config.json）—— 退化成单服务，端口来自 <c>--urls</c> / Kestrel 默认，
/// 密码来自 <c>--token</c>。不写配置文件的用法全部落在这里，行为不变。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ServiceCatalog
{
    public IReadOnlyList<ServiceProfile> Services { get; }

    /// <summary>是否来自 config.json。为 false 时 Kestrel 走原来的 <c>--urls</c> 路径。</summary>
    public bool IsExplicit { get; }

    /// <summary>配置文件路径（没有就是 null），仅用于日志。</summary>
    public string? ConfigPath { get; }

    private ServiceCatalog(IReadOnlyList<ServiceProfile> services, bool isExplicit, string? configPath)
    {
        Services = services;
        IsExplicit = isExplicit;
        ConfigPath = configPath;
    }

    /// <summary>端口对不上时（理论上只会发生在测试管线或 Kestrel 额外绑了端口）的兜底。</summary>
    public ServiceProfile Default => Services[0];

    public ServiceProfile? ByPort(int port)
    {
        foreach (var service in Services)
        {
            if (service.Port == port) return service;
        }

        return null;
    }

    /// <summary>
    /// 由已加载好的配置解析出服务列表。<paramref name="configPath"/> 为 null 表示没有配置文件，
    /// 走传统单服务模式。
    /// </summary>
    public static ServiceCatalog Resolve(ServerOptions options, string? configPath, MediaRackConfig config)
    {
        if (configPath is null)
        {
            // 传统模式：一个服务，端口沿用 --urls 推导出来的那个。
            var legacy = new ServiceProfile
            {
                Name = options.Token is null ? "全部" : "受保护",
                Port = options.Port,
                Password = options.Token,
                Writable = true,
                Sources = [],
            };

            return new ServiceCatalog([legacy], isExplicit: false, configPath: null);
        }

        var services = new List<ServiceProfile>();
        var seen = new HashSet<int>();

        foreach (var entry in config.Services)
        {
            if (entry.Port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"配置里的服务端口不合法：{entry.Port}（必须在 1..65535 之间）。文件：{configPath}");
            }

            // 同端口监听两次在 Kestrel 里是个含糊的错误，这里先给出人话。
            if (!seen.Add(entry.Port))
            {
                throw new InvalidOperationException(
                    $"配置里有两个服务用了同一个端口 {entry.Port}。每个服务必须有独立端口。文件：{configPath}");
            }

            services.Add(new ServiceProfile
            {
                Name = string.IsNullOrWhiteSpace(entry.Name) ? $"服务 {entry.Port}" : entry.Name.Trim(),
                Port = entry.Port,
                Password = string.IsNullOrWhiteSpace(entry.Password) ? null : entry.Password.Trim(),
                Writable = entry.Writable,
                Sources = entry.Sources ?? [],
            });
        }

        if (services.Count == 0) services.AddRange(MediaRackConfig.Default.Services.Select(ToProfile));

        return new ServiceCatalog(services, isExplicit: true, configPath: configPath);
    }

    /// <summary>把默认配置里的条目转成运行期模型。只给"配置里一个服务都没写"的分支用。</summary>
    private static ServiceProfile ToProfile(ServiceConfig entry) => new()
    {
        Name = entry.Name ?? "全部",
        Port = entry.Port,
        Password = entry.Password,
    };
}
