using MediaRack.Models;
using MediaRack.Store;

namespace MediaRack.Server.Json;

/// <summary>库模型 → 契约 DTO。所有字段的兜底规则集中在这里，端点里不再各自拼。</summary>
public static class DtoMapper
{
    public static string LayoutName(SourceLayout layout) => layout == SourceLayout.Dual ? "dual" : "single";

    public static string VirtualWriteName(VirtualWrite level) => level switch
    {
        VirtualWrite.None => "none",
        VirtualWrite.Whitelist => "whitelist",
        _ => "full",
    };

    public static SourceDto ToDto(SourceConfig source, int itemCount) => new(
        Id: source.Id,
        Name: source.Name,
        Layout: LayoutName(source.Layout),
        VirtualWrite: VirtualWriteName(source.VirtualWrite),
        Enabled: source.Enabled,
        Reachable: Reachable(source),
        ItemCount: itemCount,
        LastScanAt: source.LastScanAt);

    /// <summary>
    /// 源是否可用 = 至少一个文件夹在位（移动硬盘只挂了一半时，另一半的条目会 missing，
    /// 但库里还能看到可达的那部分，比一刀切"整个源不可达"诚实）。
    /// </summary>
    public static bool Reachable(SourceConfig source) =>
        source.Folders.Count > 0
            ? source.Folders.Any(f => Directory.Exists(f.Root))
            : Directory.Exists(source.Root ?? string.Empty);

    public static MediaSummaryDto ToSummary(MediaSummaryRow row) => new(
        Key: row.Key,
        SourceId: row.SourceId,
        Layout: row.Layout,
        // 契约里 title 是必填字符串：没有标题时给空串，而不是让客户端处理 null。
        Title: row.Title ?? string.Empty,
        UpName: row.UpName,
        DurationMs: row.DurationMs,
        Width: row.Width,
        Height: row.Height,
        SizeBytes: row.SizeBytes,
        TagCode: row.TagCode,
        PosterUrl: PosterUrl(row.SourceId, row.Key),
        HasDanmaku: row.HasDanmaku,
        Hidden: row.Hidden,
        PlayPosMs: row.PlayPosMs,
        Rotation: row.Rotation,
        Status: row.Status,
        Sources: row.Sources.Count > 0 ? row.Sources : null,
        Preferred: row.Sources.Count > 0 ? row.Preferred : null,
        CollectionId: row.CollectionId,
        CollectionName: row.CollectionName,
        CollectionKind: row.CollectionKind);

    public static MediaMetaDto ToDto(MediaMeta meta) => new(
        TitleDisplay: meta.TitleDisplay,
        Tags: meta.Tags,
        Hidden: meta.Hidden,
        Rotation: meta.Rotation,
        PlayPosMs: meta.PlayPosMs,
        PlayCount: meta.PlayCount,
        LastPlayAt: meta.LastPlayAt,
        Rev: meta.Rev,
        Collection: meta.Collection);

    public static string PosterUrl(string sourceId, string key) =>
        $"/poster/{Uri.EscapeDataString(sourceId)}/{Uri.EscapeDataString(key)}.jpg";
}
