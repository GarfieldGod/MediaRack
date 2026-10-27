using System.Reflection;
using MediaRack.Library;
using MediaRack.Server.Hosting;
using MediaRack.Server.Json;
using MediaRack.Store;

namespace MediaRack.Server.Endpoints;

internal static class OpsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/health", (HttpContext context, LibraryAccess lib, Capabilities caps,
            ServiceScope scope) => lib.With(db =>
        {
            // 按服务过滤：加密服务不该借健康检查把"这台机器上还有哪些源"报出来。
            var sources = new SourceRepository(db).List()
                .Where(s => scope.CanSee(s.Id))
                .ToList();

            var items = sources
                .Select(s => new SourceHealthDto(
                    Id: s.Id,
                    Reachable: DtoMapper.Reachable(s),
                    ItemCount: (int)db.CountMedia(s.Id)))
                .ToList();

            // 有任何源不可达就报 degraded，而不是假装一切正常——
            // 移动硬盘没插是最常见的情况，客户端据此显示灰度。
            var status = items.All(i => i.Reachable) ? "ok" : "degraded";

            // 探活要免鉴权（监控、负载均衡都拿不到令牌），所以这里在未认证时不吐磁盘路径：
            // 状态码和源数量足够判断死活，路径属于"知道得越少越好"的那一类。
            var password = scope.Profile.Password;
            var authenticated = password is null ||
                Auth.IsAuthorized(context.Request, password, scope.Profile.CookieName);

            return Results.Ok(new HealthDto(
                Status: status,
                Version: Version(),
                SchemaVersion: db.Version,
                Sources: items,
                Capabilities: new CapabilitiesDto(caps.Ffmpeg, caps.HardwareEncoder),
                MetaRoot: authenticated ? lib.MetaRoot : null,
                AuthRequired: password is not null));
        }));

        app.MapGet("/api/v1/export/{source}/{key}.mp4",
            (string source, string key, LibraryAccess lib, Capabilities caps,
                ExportService exports, ILoggerFactory loggerFactory, ServiceScope scope) =>
                lib.With<IResult>(db =>
            {
                var config = new SourceRepository(db).Get(source);
                if (config is null) return Api.SourceNotFound(source);
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                var entry = new MediaRepository(db).Get(source, key);
                if (entry is null) return Api.MediaNotFound(source, key);

                // 单流源本来就是单流，再转一份只会白占空间。
                var single = MediaPaths.SingleFile(config, entry);
                if (single is not null)
                {
                    return Results.File(single, MediaPaths.ContentTypeFor(single), enableRangeProcessing: true);
                }

                var ready = exports.TryGetReady(source, key);
                if (ready is not null)
                {
                    exports.Touch(ready);
                    return Results.File(ready, "video/mp4", enableRangeProcessing: true);
                }

                if (!caps.Ffmpeg)
                {
                    return Api.Conflict("capability_unavailable", "本机没有 ffmpeg，无法转封装");
                }

                // 202 的语义是"已经开始了，过一会儿对同一个 URL 再来一次"。
                // 正在构建时重复请求也回 202，不会重复起 ffmpeg（ExportService 内部按 key 串行）。
                if (!exports.IsBuilding(source, key))
                {
                    var logger = loggerFactory.CreateLogger("MediaRack.Export");
                    exports.StartBackground(source, key,
                        ex => logger.LogError(ex, "导出失败 {Source}/{Key}", source, key));
                }

                return Results.Json(new { status = "building" }, statusCode: StatusCodes.Status202Accepted);
            }));
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}
