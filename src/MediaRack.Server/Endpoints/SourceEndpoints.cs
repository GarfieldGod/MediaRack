using MediaRack.Server.Hosting;
using MediaRack.Server.Json;
using MediaRack.Store;

namespace MediaRack.Server.Endpoints;

internal static class SourceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // 只列当前服务能看到的源。看不到的源连名字都不该出现——
        // 否则加密服务里也能数出"这台机器上还有 5 个源"。
        app.MapGet("/api/v1/sources", (LibraryAccess lib, ServiceScope scope) => lib.With(db =>
        {
            var sources = new SourceRepository(db).List();

            var items = sources
                .Where(s => scope.CanSee(s.Id))
                .Select(s => DtoMapper.ToDto(s, (int)db.CountMedia(s.Id)))
                .ToList();

            return Results.Ok(new { sources = items });
        }));
    }
}
