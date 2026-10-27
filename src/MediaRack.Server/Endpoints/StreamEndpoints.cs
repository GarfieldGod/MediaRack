using MediaRack.Library;
using MediaRack.Models;
using MediaRack.Server.Hosting;
using MediaRack.Server.Json;
using MediaRack.Store;

namespace MediaRack.Server.Endpoints;

/// <summary>
/// 流端点。全部走 <c>Results.File(..., enableRangeProcessing: true)</c>——
/// 框架直接按 Range 切片发送，<b>服务端一个字节都不重新编码</b>，这是 4K 播放 CPU 接近 0 的原因。
/// </summary>
internal static class StreamEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/media/{source}/{key}/video.m4s",
            (string source, string key, LibraryAccess lib, ServiceScope scope) => lib.With<IResult>(db =>
            {
                var config = new SourceRepository(db).Get(source);
                if (config is null) return Api.SourceNotFound(source);
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                var entry = new MediaRepository(db).Get(source, key);
                if (entry is null) return Api.MediaNotFound(source, key);

                if (entry.Layout != SourceLayout.Dual)
                {
                    return Api.Error(StatusCodes.Status404NotFound, "not_dual",
                        $"该条目是 single 布局，请用 /media/{source}/{key}/file");
                }

                var path = MediaPaths.Video(config, entry);
                if (path is null) return Api.MediaNotFound(source, key);

                return Results.File(
                    path,
                    MediaPaths.ContentTypeForTrack(path, TrackRole.Video),
                    enableRangeProcessing: true);
            }));

        app.MapGet("/media/{source}/{key}/audio.m4s",
            (string source, string key, LibraryAccess lib, ServiceScope scope) => lib.With<IResult>(db =>
            {
                var config = new SourceRepository(db).Get(source);
                if (config is null) return Api.SourceNotFound(source);
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                var entry = new MediaRepository(db).Get(source, key);
                if (entry is null) return Api.MediaNotFound(source, key);

                if (entry.Layout != SourceLayout.Dual)
                {
                    return Api.Error(StatusCodes.Status404NotFound, "not_dual",
                        $"该条目是 single 布局，请用 /media/{source}/{key}/file");
                }

                var path = MediaPaths.Audio(config, entry);
                if (path is null)
                {
                    return Api.Error(StatusCodes.Status404NotFound, "track_not_found",
                        $"该条目没有音频轨：{source}/{key}");
                }

                return Results.File(path, "audio/mp4", enableRangeProcessing: true);
            }));

        app.MapGet("/media/{source}/{key}/file",
            (string source, string key, LibraryAccess lib, ServiceScope scope) => lib.With<IResult>(db =>
            {
                var config = new SourceRepository(db).Get(source);
                if (config is null) return Api.SourceNotFound(source);
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                var entry = new MediaRepository(db).Get(source, key);
                if (entry is null) return Api.MediaNotFound(source, key);

                if (entry.Layout != SourceLayout.Single)
                {
                    return Api.Error(StatusCodes.Status404NotFound, "not_single",
                        $"该条目是 dual 布局，请分别请求 video.m4s 与 audio.m4s");
                }

                var path = MediaPaths.SingleFile(config, entry);
                if (path is null) return Api.MediaNotFound(source, key);

                return Results.File(path, MediaPaths.ContentTypeFor(path), enableRangeProcessing: true);
            }));

        // 逃生舱：消费方不认双流时只换这条 URL，播放器代码不用改。
        app.MapGet("/play/{source}/{key}",
            (string source, string key, HttpRequest request,
                LibraryAccess lib, Capabilities caps, ExportService exports, ServiceScope scope) =>
                lib.With<IResult>(db =>
            {
                var config = new SourceRepository(db).Get(source);
                if (config is null) return Api.SourceNotFound(source);
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                var entry = new MediaRepository(db).Get(source, key);
                if (entry is null) return Api.MediaNotFound(source, key);

                var mode = (Api.StrQuery(request, "mode") ?? "auto").ToLowerInvariant();

                switch (mode)
                {
                    case "transcode":
                        return Api.Conflict("capability_unavailable",
                            "transcode 尚未实现。请改用 mode=remux 或 direct。");

                    case "direct":
                        // 一条 307 装不下两条流的地址，所以这里指向详情：
                        // 客户端从 tracks[] 里取两条 URL，自己交给播放器合成。
                        // 必须是 307（保留方法）而不是 302，否则 POST 语义会被悄悄改掉。
                        return Results.Redirect(
                            $"/api/v1/media/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(key)}",
                            permanent: false,
                            preserveMethod: true);

                    case "auto":
                    case "remux":
                        break;

                    default:
                        return Api.BadRequest($"mode 只接受 auto / direct / remux / transcode，收到 {mode}");
                }

                var single = MediaPaths.SingleFile(config, entry);
                if (single is not null)
                {
                    // 单流源本来就是单文件，remux 等于原样返回。
                    return Results.File(single, MediaPaths.ContentTypeFor(single), enableRangeProcessing: true);
                }

                if (!caps.Ffmpeg)
                {
                    return Api.Conflict("capability_unavailable",
                        "本机没有 ffmpeg，无法转封装。请用 mode=direct 自行合成双流。");
                }

                try
                {
                    var path = exports.Ensure(source, key, request.HttpContext.RequestAborted);
                    return Results.File(path, "video/mp4", enableRangeProcessing: true);
                }
                catch (ExportException ex)
                {
                    return ex.Kind switch
                    {
                        ExportErrorKind.NotFound => Api.MediaNotFound(source, key),
                        ExportErrorKind.NoFfmpeg => Api.Conflict("capability_unavailable", ex.Message),
                        _ => Api.Error(StatusCodes.Status500InternalServerError, "remux_failed", ex.Message),
                    };
                }
            }));

        app.MapGet("/poster/{source}/{key}.jpg",
            (string source, string key, LibraryAccess lib, ServerOptions options, ServiceScope scope) =>
                lib.With<IResult>(db =>
            {
                // 封面也是内容：从可见性上必须和条目本身一致，否则加密服务里能把看不到的源的海报全拉走。
                if (!scope.CanSee(source)) return Api.MediaNotFound(source, key);

                var entry = new MediaRepository(db).Get(source, key);
                if (entry is null) return Api.MediaNotFound(source, key);

                var path = MediaPaths.Poster(options, entry);
                if (path is null)
                {
                    // 抽帧尚未生成（或已被清掉）。客户端显示占位图，不要当错误弹窗。
                    return Api.Error(StatusCodes.Status404NotFound, "poster_not_found",
                        $"尚无缩略图：{source}/{key}");
                }

                return Results.File(path, "image/jpeg", enableRangeProcessing: true);
            }));

        // 首帧缩略图（信息流连贯翻页用）：抽视频第 0 帧，高 720 落缓存。
        // 与 /poster（中点帧 ≤560px 封面）互补——翻页落位瞬间首帧与播放画面无缝衔接。
        app.MapGet("/media/{source}/{key}/thumb.jpg",
            (string source, string key, HttpRequest request,
                LibraryAccess lib, Capabilities caps, ThumbService thumbs, ServiceScope scope) =>
                lib.With<IResult>(db =>
            {
                var config = new SourceRepository(db).Get(source);
                if (config is null) return Api.SourceNotFound(source);
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                var entry = new MediaRepository(db).Get(source, key);
                if (entry is null) return Api.MediaNotFound(source, key);

                var ready = thumbs.TryGetReady(source, key);
                if (ready is not null)
                {
                    thumbs.Touch(ready);
                    return Results.File(ready, "image/jpeg");
                }

                if (!caps.Ffmpeg)
                {
                    return Api.Error(StatusCodes.Status404NotFound, "thumb_unavailable",
                        "本机没有 ffmpeg，无法生成首帧缩略图");
                }

                try
                {
                    var path = thumbs.Ensure(source, key, request.HttpContext.RequestAborted);
                    return Results.File(path, "image/jpeg");
                }
                catch (ExportException ex)
                {
                    return ex.Kind switch
                    {
                        ExportErrorKind.NotFound => Api.MediaNotFound(source, key),
                        ExportErrorKind.NoFfmpeg => Api.Error(StatusCodes.Status404NotFound, "thumb_unavailable", ex.Message),
                        _ => Api.Error(StatusCodes.Status500InternalServerError, "thumb_failed", ex.Message),
                    };
                }
            }));
    }
}
