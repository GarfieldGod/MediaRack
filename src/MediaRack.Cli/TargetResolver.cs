using MediaRack.Store;

namespace MediaRack.Cli;

/// <summary>
/// 把用户给的 <c>&lt;key&gt;</c> 解析成唯一的 <c>(source, key)</c>。
/// <para>
/// 关键点：<b>同一个 key 可以同时存在于多个源</b>（这正是多源聚合的前提）。
/// 所以不指定 <c>--source</c> 时不猜——只有一个候选就用它，多个候选就报错并列出，
/// 让用户明确选，避免"我以为改的是这个源"。
/// </para>
/// </summary>
internal static class TargetResolver
{
    public static (string SourceId, string Key) Resolve(
        LibraryDatabase db, string? sourceId, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new CliException("缺少条目 key");

        var repo = new MediaRepository(db);

        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            if (repo.SourcesForKey(key).Contains(sourceId, StringComparer.Ordinal))
            {
                return (sourceId, key);
            }

            throw new CliException($"源 {sourceId} 里没有条目 {key}");
        }

        var sources = repo.SourcesForKey(key);
        return sources.Count switch
        {
            0 => throw new CliException($"条目不存在：{key}"),
            1 => (sources[0], key),
            _ => throw new CliException(
                $"条目 {key} 同时存在于 {sources.Count} 个源（{string.Join(", ", sources)}），请用 --source 指定"),
        };
    }

    /// <summary><c>a,b,c</c> 或 <c>a，b，c</c> → 列表。中英文逗号都认。</summary>
    public static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
