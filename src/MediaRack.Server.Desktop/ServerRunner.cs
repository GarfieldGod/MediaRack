using MediaRack.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace MediaRack.Server.Desktop;

/// <summary>
/// 托盘壳对服务生命周期的全部了解。界面只跟它打交道，不直接碰 Kestrel。
/// <para>
/// 单进程形态：WebApplication 就跑在这个 WinForms 进程里。
/// 所以"退出托盘"= 服务真的停了，不存在"关了窗口服务还在后台跑"的那种分裂状态。
/// </para>
/// </summary>
internal sealed class ServerRunner
{
    private WebApplication? _app;

    public bool Running => _app is not null;

    /// <summary>启动失败的原因，给托盘气泡用。</summary>
    public string? LastError { get; private set; }

    public IReadOnlyList<ServiceProfile> Services { get; private set; } = [];

    /// <summary>点托盘"打开管理页"时去哪个地址。取第一个服务，它通常是"全部"。</summary>
    public string? PrimaryUrl =>
        Services.Count == 0 ? null : $"http://127.0.0.1:{Services[0].Port}";

    /// <summary>桌面壳的日志位置。没有控制台，出问题只能看这个文件。</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaRack", "logs", "desktop.log");

    /// <summary>
    /// 按配置启动（或重启）。返回 false 时 <see cref="LastError"/> 有原因，
    /// <b>不要吞掉它</b>——最常见的是端口被占用，用户需要知道是哪个端口。
    /// </summary>
    public async Task<bool> StartAsync(MediaRackConfig config)
    {
        await StopAsync();
        LastError = null;

        try
        {
            var metaRoot = string.IsNullOrWhiteSpace(config.MetaRoot)
                ? ServerOptions.DefaultMetaRoot
                : Path.GetFullPath(config.MetaRoot);

            var options = new ServerOptions
            {
                MetaRoot = metaRoot,
                // 桌面壳自己决定什么时候开浏览器（点菜单/首次配置完），不让服务端抢这个动作。
                Open = false,
            };

            // 传 UserPath 而不是 null：null 会退回"传统单服务模式"，
            // 那样 config.json 里配的多服务与权限就全被忽略了。
            var catalog = ServiceCatalog.Resolve(options, MediaRackConfig.UserPath, config);

            _app = MediaRackHost.Build(options, catalog, builder =>
            {
                // WinExe 没有控制台：默认 Console provider 写出去的中文是乱码，且用户看不到。
                builder.Logging.ClearProviders();
                builder.Logging.AddProvider(new FileLoggerProvider(LogPath));
                builder.Logging.SetMinimumLevel(LogLevel.Information);
            });

            await _app.StartAsync();
            Services = catalog.Services;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
            await StopAsync();
            Services = [];
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;

        var app = _app;
        _app = null;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token);
        }
        catch (Exception)
        {
            // 停止阶段的异常不该阻止进程退出：Kestrel 在端口已被释放时可能抛。
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
