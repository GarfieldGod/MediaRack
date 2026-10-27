namespace MediaRack.Media;

using MediaRack.Models;

/// <summary>
/// 把"库里的相对路径"还原成"磁盘上的绝对路径"。
/// <para>
/// 只做拼接与存在性检查，<b>不写入</b>。这是全项目唯一允许把 <c>rel_path</c> 变成绝对路径的地方。
/// </para>
/// </summary>
public static class MediaLocator
{
    /// <summary>
    /// 多文件夹版：按文件夹登记顺序逐个尝试（文件夹不相交时唯一命中）。
    /// 条目的 rel_path 相对"扫描它时的文件夹根"，所以定位只能在文件夹候选里找。
    /// </summary>
    public static string? FindPrimaryVideo(
        SourceConfig source, string relPath, SourceLayout layout, int? tagCode)
    {
        foreach (var root in RootCandidates(source))
        {
            var hit = FindPrimaryVideo(root, relPath, layout, tagCode);
            if (hit is not null) return hit;
        }

        return null;
    }

    /// <summary>定位用的文件夹根候选。老代码路径（单文件夹视角的 SourceConfig）只有 Root 一项。</summary>
    public static IReadOnlyList<string> RootCandidates(SourceConfig source)
    {
        if (source.Folders.Count > 0) return source.Folders.Select(f => f.Root).ToList();
        return string.IsNullOrEmpty(source.Root) ? [] : [source.Root];
    }

    /// <summary>
    /// 找到条目的主视频文件。<c>single</c> 源就是 <c>rel_path</c> 本身；
    /// <c>dual</c> 源是 <c>&lt;rel_path&gt;/&lt;tag&gt;/video.m4s</c>（tag 缺失时向下探一层）。
    /// </summary>
    public static string? FindPrimaryVideo(
        string sourceRoot, string relPath, SourceLayout layout, int? tagCode)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;

        var basePath = Combine(sourceRoot, relPath);

        if (layout == SourceLayout.Single)
        {
            return File.Exists(basePath) ? basePath : null;
        }

        if (!Directory.Exists(basePath)) return null;

        if (tagCode is { } tag)
        {
            var tagged = Path.Combine(basePath, tag.ToString());
            var video = Path.Combine(tagged, "video.m4s");
            if (File.Exists(video)) return video;
        }

        // tag 未知（entry.type_tag 缺失）时退化为"第一个含 video.m4s 的子目录"。
        foreach (var sub in SafeSubdirectories(basePath))
        {
            var video = Path.Combine(sub, "video.m4s");
            if (File.Exists(video)) return video;
        }

        return null;
    }

    /// <summary>把库里的相对路径（统一用 <c>/</c>）拼成当前平台的绝对路径。</summary>
    public static string Combine(string root, string relPath) =>
        Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));

    private static IEnumerable<string> SafeSubdirectories(string dir)
    {
        string[] items;
        try
        {
            items = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var item in items) yield return item;
    }
}
