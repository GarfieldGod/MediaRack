using System.Reflection;
using System.Text.Json;
using MediaRack.Library;
using MediaRack.Models;
using MediaRack.Server.Endpoints;
using MediaRack.Server.Jobs;
using MediaRack.Server.Json;
using MediaRack.Store;
using Microsoft.AspNetCore.Hosting;

namespace MediaRack.Server.Hosting;

/// <summary>
/// 把整个 HTTP 管线装起来。两个入口共用：命令行（<c>mediarack-server</c>）与桌面托盘壳（<c>MediaRack.Server.Desktop</c>）。
/// <para>
/// 抽出来的理由不是"复用几行代码"，而是<b>两个入口必须跑在同一条管线上</b>——
/// 桌面壳里点"重新扫描"和命令行里打 <c>--scan</c>，走的必须是同一段逻辑，
/// 否则迟早出现"GUI 能播、命令行不能"这种没法查的分叉。
/// </para>
/// </summary>
public static class MediaRackHost
{
    /// <summary>命令行入口。配置来自 <c>appsettings.json</c> + 命令行参数 + <c>config.json</c>。</summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var (configPath, fileConfig) = ResolveFileConfig(builder, args);
        var options = ServerOptions.From(builder.Configuration, args, fileConfig);
        var catalog = ServiceCatalog.Resolve(options, configPath, fileConfig);

        return Compose(builder, options, catalog);
    }

    /// <summary>
    /// 找出本次要用的配置。
    /// <para>
    /// 测试管线<strong>绝不</strong>读用户的真实 config.json：否则本机 %LOCALAPPDATA% 里放着一个
    /// 三服务配置，测试就会莫名其妙起三个端口。改由 <c>MediaRack:Services</c> 这个配置键注入
    /// 一段 JSON，测试想要多服务就自己给。
    /// </para>
    /// </summary>
    private static (string? Path, MediaRackConfig Config) ResolveFileConfig(
        WebApplicationBuilder builder, string[] args)
    {
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            var realPath = MediaRackConfig.Locate(args);
            return (realPath, MediaRackConfig.Load(realPath));
        }

        var inline = builder.Configuration["MediaRack:Services"];
        if (string.IsNullOrWhiteSpace(inline)) return (null, MediaRackConfig.Default);

        // configPath 给一个非 null 的哨兵值：它同时是"走显式多服务模式"的开关。
        // 键里装的是<b>服务数组</b>，不是完整配置对象。
        return ("<testing>", MediaRackConfig.FromServicesJson(inline));
    }

    /// <summary>
    /// 桌面壳入口：配置已经由界面加载好，不再碰命令行与 appsettings。
    /// <para>
    /// <paramref name="configure"/> 给壳留下调日志的口子：WinExe 没有控制台，
    /// 默认的 Console provider 会把中文写成乱码，必须换成文件日志。
    /// </para>
    /// </summary>
    public static WebApplication Build(
        ServerOptions options, ServiceCatalog catalog, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MediaRackHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        configure?.Invoke(builder);

        return Compose(builder, options, catalog);
    }

    private static WebApplication Compose(
        WebApplicationBuilder builder, ServerOptions options, ServiceCatalog catalog)
    {
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(catalog);

        // 每请求一份：当前落在哪个端口，只能按请求算，不能全局算。
        builder.Services.AddScoped<ServiceScope>();

        // 能力探测会启两个进程（ffmpeg -version），只在启动时跑一次。
        builder.Services.AddSingleton(_ => Capabilities.Detect());
        builder.Services.AddSingleton(_ => new ExportService(options.MetaRoot, options.ExportBudgetBytes));
        builder.Services.AddSingleton(_ => new ThumbService(options.MetaRoot, options.ThumbBudgetBytes));
        builder.Services.AddSingleton<LibraryAccess>();
        builder.Services.AddSingleton<ScanJobQueue>();

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            // 契约里的字段是 camelCase；不要忽略 null，客户端靠 null 判断"没有覆盖"。
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.Never;
        });

        if (catalog.IsExplicit && !builder.Environment.IsEnvironment("Testing"))
        {
            // 有 config.json 就以它为准，把 --urls 清空，否则 Kestrel 会额外绑一个默认端口
            // （表现为"我明明只配了 8081，怎么 5000 也开着"）。
            builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                foreach (var service in catalog.Services) kestrel.ListenAnyIP(service.Port);
            });
        }

        var app = builder.Build();

        var access = app.Services.GetRequiredService<LibraryAccess>();
        var capabilities = app.Services.GetRequiredService<Capabilities>();

        app.Logger.LogInformation("MediaRack 库目录：{MetaRoot}", options.MetaRoot);
        app.Logger.LogInformation(catalog.ConfigPath is null
            ? "未使用配置文件，按单服务模式启动"
            : "配置文件：{Path}", catalog.ConfigPath ?? string.Empty);

        foreach (var service in catalog.Services)
        {
            app.Logger.LogInformation(
                "服务 [{Name}] 端口 {Port}｜{Auth}｜可见源：{Sources}｜{Write}",
                service.Name,
                service.Port,
                service.IsPublic ? "公开" : "需要密码",
                service.SeesAllSources ? "全部" : string.Join(", ", service.Sources),
                service.Writable ? "可写" : "只读");
        }

        if (!capabilities.Ffmpeg)
        {
            app.Logger.LogWarning("ffmpeg 不可用，remux 与 single 源探测将降级：{Error}", capabilities.Error);
        }

        // 建库/迁移放在启动阶段：之后就假定库一定存在，读路径上不必再处理"库还没建"。
        try
        {
            access.Initialize();
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "无法初始化库：{MetaRoot}", options.MetaRoot);
            throw;
        }

        // 每个源的入口地址要打出来：日志是这台机器上唯一能"看到全部源"的地方，
        // 漏打一个源就等于那个源没法从别的设备访问（地址得靠猜）。
        try
        {
            foreach (var source in access.With(db => new SourceRepository(db).List()))
            {
                app.Logger.LogInformation(
                    "源 [{Name}] /s/{Slug}｜{Auth}｜{Write}",
                    source.Name,
                    source.Slug,
                    string.IsNullOrEmpty(source.Token) ? "无令牌（不可访问）" : "令牌已配",
                    source.VirtualWrite == VirtualWrite.None ? "只读" : "可写");
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "读取源列表失败，跳过入口播报");
        }

        // ① 源门户：先判"这个请求属于谁"。/s/{slug} 走库里的源，根路径走端口服务且只认本机。
        //    必须排在鉴权之前——鉴权要读的就是它 Bind 出来的那个 profile。
        app.Use(async (context, next) =>
        {
            var scope = context.RequestServices.GetRequiredService<ServiceScope>();
            var access = context.RequestServices.GetRequiredService<LibraryAccess>();
            await SourcePortal.InvokeAsync(context, next, access, scope);
        });

        // ② 路由必须显式放在门户之后。<b>不写这一行是拿不到任何门户请求的</b>：
        //    WebApplication.Build() 发现用户没显式调 UseRouting，会把它隐式插到管线最前面，
        //    路由就拿着原始的 /s/{slug}/… 去匹配——表里没有这个模式，门户剥完前缀也晚了，
        //    全部 404 且响应体为空（端点路由的默认 404，不是任何业务代码写的）。
        app.UseRouting();

        // ③ 鉴权兜底：门户已经判完的请求直接放行，剩下的按端口服务校验。
        //    两件事必须挨在一起：Bind 与校验之间如果插进别的中间件，那个中间件就会在
        //    "未鉴权但已确定服务"的状态下处理请求，很容易被后来者误用成免鉴权通道。
        app.Use(async (context, next) =>
        {
            var scope = context.RequestServices.GetRequiredService<ServiceScope>();

            if (scope.Bound)
            {
                await next();
                return;
            }

            // 端口对不上时回落到第一个服务。真实启动路径上不会发生（Kestrel 只绑配置过的端口），
            // 但测试管线没有真实端口，需要这条兜底。
            scope.Bind(catalog.ByPort(context.Connection.LocalPort) ?? catalog.Default);

            var password = scope.Profile.Password;
            if (password is null || SourcePortal.IsPublicPath(context.Request.Path.Value ?? "/"))
            {
                await next();
                return;
            }

            if (!Auth.IsAuthorized(context.Request, password, scope.Profile.CookieName))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorDto("unauthorized", "需要令牌"));
                return;
            }

            await next();
        });

        SourceEndpoints.Map(app);
        LibraryEndpoints.Map(app);
        MediaEndpoints.Map(app);
        StreamEndpoints.Map(app);
        ManageEndpoints.Map(app);
        AdminEndpoints.Map(app);
        OpsEndpoints.Map(app);
        AuthEndpoints.Map(app);
        MapEmbeddedWeb(app);

        app.Lifetime.ApplicationStarted.Register(() => Announce(app, options, catalog));

        return app;
    }

    /// <summary>地址要等 Kestrel 真正绑好端口才知道（<c>0.0.0.0</c> 会展开成每张网卡一条）。</summary>
    private static void Announce(WebApplication app, ServerOptions options, ServiceCatalog catalog)
    {
        var groups = catalog.Services
            .Select(service => new EndpointAnnouncer.ServiceEndpoints(
                service.Name,
                Locked: !service.IsPublic,
                Endpoints: EndpointAnnouncer.Expand([$"http://0.0.0.0:{service.Port}"])))
            .ToList();

        var endpoints = groups.SelectMany(g => g.Endpoints).ToList();

        if (catalog.IsExplicit) EndpointAnnouncer.AnnounceServices(groups);
        else EndpointAnnouncer.Announce(EndpointAnnouncer.Expand(app.Urls));

        if (!options.Open || endpoints.Count == 0) return;

        // 优先回环：绑了 VPN 网卡的机器上，拿 VPN 地址开浏览器会绕一大圈。
        var target = endpoints.FirstOrDefault(e => e.Kind == "本机") ?? endpoints[0];
        EndpointAnnouncer.OpenBrowser(target.Url);
    }

    // ── 内嵌 Web 管理页 ──────────────────────────────────────────
    // 直接把 wwwroot 编进程序集，不依赖 wwwroot 目录是否随 exe 一起拷走，
    // 也不用为了三个文件引入前端构建链。资源名形如
    // MediaRack.Server.wwwroot.index.html（目录分隔符被换成点）。
    private static void MapEmbeddedWeb(WebApplication app)
    {
        const string prefix = "MediaRack.Server.wwwroot.";
        var assembly = Assembly.GetExecutingAssembly();

        var pages = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            pages[name[prefix.Length..]] = buffer.ToArray();
        }

        if (pages.Count == 0)
        {
            app.Logger.LogWarning("没有找到内嵌的 Web 页面资源，管理页不可用");
            return;
        }

        foreach (var (file, bytes) in pages)
        {
            var contentType = ContentTypeFor(file);
            app.MapGet("/" + file, () => Results.Bytes(bytes, contentType));
        }

        // 根路径给 index.html。整站只有一个页面，不需要 SPA fallback 路由。
        if (pages.TryGetValue("index.html", out var index))
        {
            app.MapGet("/", () => Results.Bytes(index, "text/html; charset=utf-8"));
        }

        app.Logger.LogInformation("Web 管理页已加载：{Files}", string.Join(", ", pages.Keys));
    }

    private static string ContentTypeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };
}
