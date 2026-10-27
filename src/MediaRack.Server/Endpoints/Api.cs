using MediaRack.Server.Json;

namespace MediaRack.Server.Endpoints;

/// <summary>错误响应与查询参数解析。各端点共用，避免每处各写一份状态码约定。</summary>
internal static class Api
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorDto(code, message), statusCode: status);

    public static IResult SourceNotFound(string id) =>
        Error(StatusCodes.Status404NotFound, "source_not_found", $"源未登记：{id}");

    public static IResult MediaNotFound(string sourceId, string key) =>
        Error(StatusCodes.Status404NotFound, "media_not_found", $"条目不存在：{sourceId}/{key}");

    public static IResult BadRequest(string message) =>
        Error(StatusCodes.Status400BadRequest, "bad_request", message);

    public static IResult Forbidden(string message) =>
        Error(StatusCodes.Status403Forbidden, "read_only_source", message);

    /// <summary>
    /// 当前服务是只读的。与 <see cref="Forbidden"/> 的区别要分清：
    /// 那个说的是"这个源不允许改"（源级，虚拟写权限），这个说的是"这个服务整体不许写"（服务级）。
    /// 客户端据此决定是灰度菜单还是整个隐藏编辑入口。
    /// </summary>
    public static IResult ServiceReadOnly(string serviceName) =>
        Error(StatusCodes.Status403Forbidden, "read_only_service",
            $"服务「{serviceName}」是只读的，不允许写操作");

    public static IResult Conflict(string code, string message) =>
        Error(StatusCodes.Status409Conflict, code, message);

    public static int IntQuery(HttpRequest request, string name, int fallback)
    {
        if (!request.Query.TryGetValue(name, out var raw)) return fallback;
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    public static bool BoolQuery(HttpRequest request, string name, bool fallback)
    {
        if (!request.Query.TryGetValue(name, out var raw)) return fallback;
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    public static string? StrQuery(HttpRequest request, string name)
    {
        if (!request.Query.TryGetValue(name, out var raw)) return null;
        var value = raw.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>分页参数，<c>limit</c> 封顶 <see cref="MaxLimit"/>——契约里写的是 500。</summary>
    public static (int Offset, int Limit) Paging(HttpRequest request)
    {
        var offset = Math.Max(0, IntQuery(request, "offset", 0));
        var limit = Math.Clamp(IntQuery(request, "limit", DefaultLimit), 1, MaxLimit);
        return (offset, limit);
    }
}
