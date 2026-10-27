using MediaRack.Server.Hosting;
using MediaRack.Server.Json;
using MediaRack.Store;

namespace MediaRack.Server.Endpoints;

internal static class LibraryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // 单源视图：一行一条，不去重。
        app.MapGet("/api/v1/sources/{source}/library",
            (string source, HttpRequest request, LibraryAccess lib, ServiceScope scope) => lib.With<IResult>(db =>
            {
                if (new SourceRepository(db).Get(source) is null) return Api.SourceNotFound(source);

                // 存在但不在本服务的可见范围里，同样报 404：403 等于确认"这个源存在"。
                if (!scope.CanSee(source)) return Api.SourceNotFound(source);

                var (offset, limit) = Api.Paging(request);
                var page = new MediaRepository(db).ListSummaries(new SummaryQuery(
                    SourceId: source,
                    Search: Api.StrQuery(request, "q"),
                    CollectionId: Api.StrQuery(request, "collection"),
                    // 单源视图也支持 includeHidden：Web 页的「显示已隐藏」在两个视图下都该有效，
                    // 否则用户会以为勾选框坏了。
                    IncludeHidden: Api.BoolQuery(request, "includeHidden", false),
                    // 文件被删（missing）的条目默认不下发；Web 管理页传 includeMissing=1 看全量。
                    IncludeMissing: Api.BoolQuery(request, "includeMissing", false),
                    Offset: offset,
                    Limit: limit,
                    FavoriteId: Api.StrQuery(request, "favorite"),
                    // sort=added：收藏夹按加入时间倒序（连续播放要求与列表显示同一顺序）
                    Sort: Api.StrQuery(request, "sort"),
                    // sort=recommend 的确定性种子（手机进信息流生成一次、会话内复用）
                    Seed: Api.StrQuery(request, "seed")));

                return Results.Ok(ToPage(page));
            }));

        // 聚合视图：同一 key 在多个源各有一条记录，这里合并成一项。
        app.MapGet("/api/v1/library", (HttpRequest request, LibraryAccess lib, ServiceScope scope) =>
            lib.With<IResult>(db =>
            {
                var (offset, limit) = Api.Paging(request);
                var page = new MediaRepository(db).ListSummaries(new SummaryQuery(
                    SourceId: null,
                    Search: Api.StrQuery(request, "q"),
                    CollectionId: Api.StrQuery(request, "collection"),
                    IncludeHidden: Api.BoolQuery(request, "includeHidden", false),
                    // 文件被删（missing）的条目默认不下发；Web 管理页传 includeMissing=1 看全量。
                    IncludeMissing: Api.BoolQuery(request, "includeMissing", false),
                    Offset: offset,
                    Limit: limit,
                    // 服务视角的可见源白名单。必须进 SQL——查完再筛会让 total 和分页游标按全库算，
                    // 表现为"总数 5427，翻两页就到底"。
                    SourceIds: scope.VisibleSourceIds,
                    // 收藏夹视图：/library?favorite=<id>，同样在 SQL 层过滤。
                    FavoriteId: Api.StrQuery(request, "favorite"),
                    Sort: Api.StrQuery(request, "sort"),
                    Seed: Api.StrQuery(request, "seed")));
                return Results.Ok(ToPage(page));
            }));
    }

    private static MediaPageDto ToPage(SummaryPage page) => new(
        Total: page.Total,
        Items: page.Items.Select(DtoMapper.ToSummary).ToList(),
        NextOffset: page.NextOffset);
}
