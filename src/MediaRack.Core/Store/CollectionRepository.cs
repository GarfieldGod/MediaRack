using Microsoft.Data.Sqlite;

namespace MediaRack.Store;

/// <summary>
/// 结构合集（v4）：源级的、排他的、只读的。<b>只查不写</b>——内容由
/// <see cref="CollectionRebuilder"/> 按 media 投影出来，用户层的东西在 <see cref="FavoriteRepository"/>。
/// <para>
/// single 文件夹源：目录即合集（只算直接父目录，平铺、无树）；dual 源：一个 UP 主一个合集。
/// 一个条目恰好属于一个合集（<c>media.collection_id</c>）。
/// </para>
/// </summary>
public sealed class CollectionRepository(LibraryDatabase db)
{
    /// <summary>某个源的全部合集（含条目数），平铺。</summary>
    public IReadOnlyList<CollectionRow> ListBySource(string sourceId) =>
        Query("WHERE c.source_id = $sid ORDER BY c.kind, c.name COLLATE NOCASE", sourceId);

    /// <summary>全部合集（聚合视图用），按源、类型、名字排序。</summary>
    public IReadOnlyList<CollectionRow> ListAll() =>
        Query("ORDER BY c.source_id, c.kind, c.name COLLATE NOCASE", null);

    /// <summary>按 id 取单个合集。id 规则见 <see cref="CollectionRebuilder"/>。</summary>
    public CollectionRow? Get(string id) =>
        Query("WHERE c.id = $sid", id).FirstOrDefault();

    /// <summary>某个源下的全部合集 id（可见性判断用，避免逐条查）。</summary>
    public IReadOnlyList<string> IdsOf(string sourceId)
    {
        var list = new List<string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM collection WHERE source_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>合集下的条目键（按 key 排序），供 CLI 与测试使用。</summary>
    public IReadOnlyList<string> ListItemKeys(string collectionId)
    {
        var list = new List<string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT key FROM media WHERE collection_id = $cid ORDER BY key;";
        cmd.Parameters.AddWithValue("$cid", collectionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    private List<CollectionRow> Query(string tail, string? id)
    {
        var rows = new List<CollectionRow>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            $"""
             SELECT c.id, c.name, c.source_id, COALESCE(c.kind, 'folder'), COALESCE(c.path, ''),
                    (SELECT COUNT(*) FROM media m WHERE m.collection_id = c.id AND m.status = 'ok')
             FROM collection c
             {tail};
             """;
        if (id is not null) cmd.Parameters.AddWithValue("$sid", id);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CollectionRow(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                SourceId: reader.GetString(2),
                Kind: reader.GetString(3),
                Path: reader.GetString(4),
                ItemCount: (int)reader.GetInt64(5)));
        }

        return rows;
    }
}

/// <summary>
/// 一个结构合集。<see cref="Kind"/> = <c>folder</c>（single 目录）或 <c>up</c>（dual UP 主）；
/// <see cref="Path"/> 是相对源的目录（folder）或 up_mid（up）。
/// </summary>
public sealed record CollectionRow(
    string Id,
    string Name,
    string SourceId,
    string Kind,
    string Path,
    int ItemCount);
