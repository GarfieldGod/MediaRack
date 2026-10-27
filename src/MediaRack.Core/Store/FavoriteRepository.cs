using Microsoft.Data.Sqlite;

namespace MediaRack.Store;

/// <summary>
/// 收藏（用户层）：多对多、跨源、可跨端同步——与结构合集完全分开。
/// <para>
/// 三张表各管一件事：<c>favorite</c>（收藏夹本身，可多个、可改名）、
/// <c>favorite_item</c>（收藏的视频）、<c>favorite_collection</c>（收藏的合集 = 关注的 UP 主 / 文件夹）。
/// </para>
/// <para>
/// 老的手工收藏夹（<c>col:</c>）在 schema v4 迁移时原样搬进来了，id 不变。
/// </para>
/// </summary>
public sealed class FavoriteRepository(LibraryDatabase db)
{
    /// <summary>默认收藏夹的 id。第一次收藏时若还没有任何收藏夹，客户端可以要这个。</summary>
    public const string DefaultId = "fav:default";

    public sealed record FavoriteRow(string Id, string Name, int SortOrder, int ItemCount, int CollectionCount);

    /// <summary>
    /// 收藏夹列表。计数**跟着视图走**：
    /// <paramref name="sourceId"/> 非空 = 只数该源；否则看
    /// <paramref name="visibleSourceIds"/>——它是"当前服务能看见哪些源"，
    /// 门户（<c>/s/{slug}</c>）只有一个源，根服务是 null（不限）。
    /// <para>
    /// 这一层不能省：门户页面和前端的「当前源」都靠它，端口服务下 null 才是全库。
    /// 漏掉它时门户会显示整台机器的收藏数——用户看到"这个源有 3 条"，
    /// 点进去只有 1 条。
    /// </para>
    /// 收藏夹本身是跨源的，但用户选中某个源时，侧栏该显示"这个源里我收藏了多少"——
    /// 否则点进夹子看到的条数与侧栏写的对不上（与合集的源级过滤同一套口径）。
    /// </summary>
    public IReadOnlyList<FavoriteRow> List(string? sourceId = null, IReadOnlyCollection<string>? visibleSourceIds = null)
    {
        // 显式指定源优先（调用方已校验过它确实可见）。
        IReadOnlyCollection<string>? allowed = sourceId is not null ? new[] { sourceId } : visibleSourceIds;

        var rows = new List<FavoriteRow>();
        using var cmd = db.Connection.CreateCommand();

        // 可见源集合按需展开成 IN 列表：空集合 = 一个源都看不见，直接 AND 0。
        var scopeCond = "";
        if (allowed is not null)
        {
            if (allowed.Count == 0)
            {
                scopeCond = " AND 0";
            }
            else
            {
                var names = new List<string>(allowed.Count);
                var n = 0;
                foreach (var s in allowed)
                {
                    var p = $"$v{n++}";
                    names.Add(p);
                    cmd.Parameters.AddWithValue(p, s);
                }
                scopeCond = $" AND i.source_id IN ({string.Join(", ", names)})";
            }
        }

        // 只数"位置记录还在"的条目：收藏跟着内容走，删源/清理缺失之后
        // 引用会留在库里等文件回来认领，但那时点进去是一条也看不到的，
        // 计数还按引用数算会变成"显示 12 条、打开是空的"。
        cmd.CommandText =
            $"""
            SELECT f.id, f.name, f.sort_order,
                   (SELECT COUNT(*) FROM favorite_item i
                      JOIN media m ON m.source_id = i.source_id AND m.key = i.key
                     WHERE i.favorite_id = f.id{scopeCond}),
                   (SELECT COUNT(*) FROM favorite_collection c WHERE c.favorite_id = f.id)
            FROM favorite f
            ORDER BY f.sort_order, f.name COLLATE NOCASE;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new FavoriteRow(
                reader.GetString(0),
                reader.GetString(1),
                (int)reader.GetInt64(2),
                (int)reader.GetInt64(3),
                (int)reader.GetInt64(4)));
        }

        return rows;
    }

    public FavoriteRow? Get(string id) => List().FirstOrDefault(f => f.Id == id);

    /// <summary>按名字找（忽略大小写）。CLI 用名字寻址。</summary>
    public FavoriteRow? FindByName(string name) =>
        List().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>新建。重名抛 <see cref="InvalidOperationException"/>——不悄悄复用别人的夹子。</summary>
    public string Create(string name, int? sortOrder = null)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("收藏夹名称不能为空", nameof(name));
        if (FindByName(trimmed) is not null) throw new InvalidOperationException($"收藏夹已存在：{trimmed}");

        var id = $"fav:{Guid.NewGuid().ToString("N")[..12]}";
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO favorite (id, name, sort_order, created_at)
            VALUES ($id, $name, $sort, $at);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", trimmed);
        cmd.Parameters.AddWithValue("$sort", sortOrder ?? (List().Count == 0 ? 0 : List().Max(f => f.SortOrder) + 1));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
        return id;
    }

    /// <summary>确保至少有一个默认收藏夹，返回它的 id。客户端第一次收藏直接用它。</summary>
    public string EnsureDefault()
    {
        if (Get(DefaultId) is { } existing) return existing.Id;
        if (List().FirstOrDefault() is { } any) return any.Id;

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO favorite (id, name, sort_order, created_at)
            VALUES ($id, '默认收藏夹', 0, $at);
            """;
        cmd.Parameters.AddWithValue("$id", DefaultId);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
        return DefaultId;
    }

    public bool Rename(string id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("收藏夹名称不能为空", nameof(name));
        var clash = FindByName(trimmed);
        if (clash is not null && clash.Id != id) throw new InvalidOperationException($"收藏夹已存在：{trimmed}");

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE favorite SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", trimmed);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>删除收藏夹及它的全部关联（不动 media、不动合集）。</summary>
    public bool Delete(string id)
    {
        using var tx = db.Connection.BeginTransaction();
        Exec("DELETE FROM favorite_item WHERE favorite_id = $fid;", id, tx);
        Exec("DELETE FROM favorite_collection WHERE favorite_id = $fid;", id, tx);
        var changed = Exec("DELETE FROM favorite WHERE id = $fid;", id, tx);
        tx.Commit();
        return changed > 0;
    }

    // ── 收藏的视频 ─────────────────────────────────────────

    public sealed record ItemRef(string SourceId, string Key);

    /// <summary>收藏的视频键（按加入时间倒序）。</summary>
    public IReadOnlyList<ItemRef> ListItems(string favoriteId)
    {
        var list = new List<ItemRef>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT source_id, key FROM favorite_item
            WHERE favorite_id = $fid
            ORDER BY added_at DESC, key;
            """;
        cmd.Parameters.AddWithValue("$fid", favoriteId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(new ItemRef(reader.GetString(0), reader.GetString(1)));
        return list;
    }

    public int AddItems(string favoriteId, IReadOnlyList<ItemRef> items)
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changed = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var item in items)
        {
            changed += Exec(
                """
                INSERT INTO favorite_item (favorite_id, source_id, key, added_at)
                VALUES ($fid, $sid, $key, $at)
                ON CONFLICT(favorite_id, source_id, key) DO NOTHING;
                """, favoriteId, tx, ("$sid", item.SourceId), ("$key", item.Key), ("$at", at));
        }

        tx.Commit();
        return changed;
    }

    public int RemoveItems(string favoriteId, IReadOnlyList<ItemRef> items)
    {
        var changed = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var item in items)
        {
            changed += Exec(
                "DELETE FROM favorite_item WHERE favorite_id = $fid AND source_id = $sid AND key = $key;",
                favoriteId, tx, ("$sid", item.SourceId), ("$key", item.Key));
        }

        tx.Commit();
        return changed;
    }

    // ── 收藏的合集（关注的 UP 主 / 文件夹）───────────────────

    public IReadOnlyList<string> ListCollections(string favoriteId)
    {
        var list = new List<string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT collection_id FROM favorite_collection WHERE favorite_id = $fid ORDER BY added_at DESC;";
        cmd.Parameters.AddWithValue("$fid", favoriteId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    public int AddCollections(string favoriteId, IReadOnlyList<string> collectionIds)
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changed = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var cid in collectionIds)
        {
            changed += Exec(
                """
                INSERT INTO favorite_collection (favorite_id, collection_id, added_at)
                VALUES ($fid, $cid, $at)
                ON CONFLICT(favorite_id, collection_id) DO NOTHING;
                """, favoriteId, tx, ("$cid", cid), ("$at", at));
        }

        tx.Commit();
        return changed;
    }

    public int RemoveCollections(string favoriteId, IReadOnlyList<string> collectionIds)
    {
        var changed = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var cid in collectionIds)
        {
            changed += Exec(
                "DELETE FROM favorite_collection WHERE favorite_id = $fid AND collection_id = $cid;",
                favoriteId, tx, ("$cid", cid));
        }

        tx.Commit();
        return changed;
    }

    /// <summary>某个视频被收藏在哪些夹子里（详情页显示"已收藏"状态用）。</summary>
    public IReadOnlyList<string> FavoritesOf(string sourceId, string key)
    {
        var list = new List<string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT favorite_id FROM favorite_item WHERE source_id = $sid AND key = $key;";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>
    /// 整体替换：这个条目只属于给定的这几个收藏夹（对应 <c>MediaPatch.collections</c> 的语义：
    /// 客户端说"它属于这几个"，不是"再加这几个"）。空列表 = 从所有收藏夹移除。
    /// </summary>
    public void ReplaceMembership(string sourceId, string key, IReadOnlyList<string> favoriteIds)
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var tx = db.Connection.BeginTransaction();

        using (var del = db.Connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM favorite_item WHERE source_id = $sid AND key = $key;";
            del.Parameters.AddWithValue("$sid", sourceId);
            del.Parameters.AddWithValue("$key", key);
            del.ExecuteNonQuery();
        }

        foreach (var fid in favoriteIds)
        {
            using var ins = db.Connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText =
                """
                INSERT INTO favorite_item (favorite_id, source_id, key, added_at)
                VALUES ($fid, $sid, $key, $at)
                ON CONFLICT(favorite_id, source_id, key) DO NOTHING;
                """;
            ins.Parameters.AddWithValue("$fid", fid);
            ins.Parameters.AddWithValue("$sid", sourceId);
            ins.Parameters.AddWithValue("$key", key);
            ins.Parameters.AddWithValue("$at", at);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private int Exec(string sql, string favoriteId, SqliteTransaction tx, params (string Name, object? Value)[] extra)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$fid", favoriteId);
        foreach (var (name, value) in extra) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }
}
