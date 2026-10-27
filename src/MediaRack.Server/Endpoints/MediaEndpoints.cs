using MediaRack.Models;
using MediaRack.Server.Hosting;
using MediaRack.Server.Json;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Server.Endpoints;

internal static class MediaEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/media/{source}/{key}",
            (string source, string key, LibraryAccess lib, Capabilities caps, ServiceScope scope) =>
                lib.With<IResult>(db =>
                {
                    var config = new SourceRepository(db).Get(source);
                    if (config is null) return Api.SourceNotFound(source);
                    if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                    var entry = new MediaRepository(db).Get(source, key);
                    if (entry is null) return Api.MediaNotFound(source, key);

                    var meta = new MetaRepository(db).Get(source, key)
                        ?? new MediaMeta { SourceId = source, Key = key };

                    return Results.Ok(Build(config, entry, meta, caps, db));
                }));

        // 这一条属于哪些收藏夹。详情侧栏的「收藏到」要把每个收藏夹的勾选状态画出来，
        // 没有这条就得为每个收藏夹各翻一遍列表（N 次分页请求），所以单开一个轻端点。
        // 返回 id 数组即可：客户端已经有收藏夹清单，只需要"是不是成员"。
        app.MapGet("/api/v1/media/{source}/{key}/favorites",
            (string source, string key, LibraryAccess lib, ServiceScope scope) =>
                lib.With<IResult>(db =>
                {
                    if (new SourceRepository(db).Get(source) is null) return Api.SourceNotFound(source);
                    if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                    // 条目本身不校验存在性：它可能刚被扫掉，但残留的收藏关系仍然要能读出来给用户取消。
                    return Results.Ok(new FavoriteRepository(db).FavoritesOf(source, key));
                }));
    }

    /// <summary>详情 DTO 的组装。<c>delivery</c> 是"这台服务器现在真能给的模式"，不是能力清单。</summary>
    public static MediaDetailDto Build(
        SourceConfig config, MediaEntry entry, MediaMeta meta, Capabilities caps,
        LibraryDatabase? db = null)
    {
        var hasAudio = entry.Layout == SourceLayout.Dual
                       && MediaPaths.Audio(config, entry) is not null;

        var delivery = new List<string> { "direct" };
        if (caps.Ffmpeg) delivery.Add("remux");

        // 所属合集：single 看目录、dual 看 UP 主（与 CollectionRebuilder 的 id 规则一致）
        CollectionRow? collection = null;
        if (db is not null)
        {
            var cid = entry.Layout == SourceLayout.Dual
                ? CollectionRebuilder.UpId(entry.SourceId,
                    string.IsNullOrEmpty(entry.UpMid) ? CollectionRebuilder.UnknownUp : entry.UpMid)
                : CollectionRebuilder.FolderId(entry.SourceId, entry.RelDir ?? string.Empty);
            collection = new CollectionRepository(db).Get(cid);
        }

        return new MediaDetailDto(
            Key: entry.Key,
            SourceId: entry.SourceId,
            Layout: DtoMapper.LayoutName(entry.Layout),
            Title: meta.TitleDisplay ?? entry.TitleRaw ?? string.Empty,
            UpName: entry.UpName,
            DurationMs: entry.DurationMs,
            Width: entry.Width,
            Height: entry.Height,
            SizeBytes: entry.SizeBytes,
            TagCode: entry.TagCode,
            PosterUrl: DtoMapper.PosterUrl(entry.SourceId, entry.Key),
            HasDanmaku: entry.HasDanmaku,
            Hidden: meta.Hidden,
            PlayPosMs: meta.PlayPosMs,
            Status: StatusName(entry.Status),
            RelPath: entry.RelPath,
            RelDir: entry.RelDir,
            Codec: entry.Codec,
            Tracks: BuildTracks(config, entry, hasAudio),
            Delivery: delivery,
            Meta: DtoMapper.ToDto(meta),
            CollectionId: collection?.Id ?? string.Empty,
            CollectionName: collection?.Name ?? string.Empty,
            CollectionKind: collection?.Kind ?? string.Empty);
    }

    private static IReadOnlyList<TrackDto> BuildTracks(
        SourceConfig config, MediaEntry entry, bool hasAudio)
    {
        var e = Uri.EscapeDataString(entry.SourceId);
        var k = Uri.EscapeDataString(entry.Key);
        var tracks = new List<TrackDto>();

        var videoTrack = entry.Tracks.FirstOrDefault(t => t.Role == TrackRole.Video);

        if (entry.Layout == SourceLayout.Dual)
        {
            tracks.Add(new TrackDto(
                Role: "video",
                Codecid: videoTrack?.CodecId,
                Bandwidth: videoTrack?.Bandwidth,
                SizeBytes: videoTrack?.SizeBytes,
                Md5: videoTrack?.Md5,
                Url: $"/media/{e}/{k}/video.m4s"));

            if (hasAudio)
            {
                var audioTrack = entry.Tracks.FirstOrDefault(t => t.Role == TrackRole.Audio);
                tracks.Add(new TrackDto(
                    Role: "audio",
                    Codecid: audioTrack?.CodecId,
                    Bandwidth: audioTrack?.Bandwidth,
                    SizeBytes: audioTrack?.SizeBytes,
                    Md5: audioTrack?.Md5,
                    Url: $"/media/{e}/{k}/audio.m4s"));
            }

            return tracks;
        }

        tracks.Add(new TrackDto(
            Role: "video",
            Codecid: videoTrack?.CodecId,
            Bandwidth: videoTrack?.Bandwidth,
            SizeBytes: videoTrack?.SizeBytes,
            Md5: videoTrack?.Md5,
            Url: $"/media/{e}/{k}/file"));

        return tracks;
    }

    public static string StatusName(MediaStatus status) => status switch
    {
        MediaStatus.Ok => "ok",
        MediaStatus.Corrupt => "corrupt",
        _ => "missing",
    };
}
