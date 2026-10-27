using MediaRack.Models;
using Microsoft.Data.Sqlite;

namespace MediaRack.Store;

/// <summary>
/// 把库里的条目重新投影成合集。**幂等**，启动时与每次扫描后都会跑一遍。
/// <para>
/// 规则（v4 定稿，用户拍板）：
/// <list type="bullet">
/// <item>single 文件夹：目录即合集，**只算直接父目录**（子目录各自成合集）。父目录自己没有视频
/// 就不成合集——平铺，不做树。</item>
/// <item>dual（B 站缓存）：一个 UP 主一个合集，**与缓存目录结构无关**（entry.json 的 owner 决定）。
/// 缺 owner 的归"未识别 UP 主"。</item>
/// <item>排他：<c>media.collection_id</c> 单列，一个视频恰好一个合集。</item>
/// </list>
/// </para>
/// <para>
/// 之所以整表重建而不是增量同步：合集只是 media 的投影，条数上万也就是一次分组查询；
/// 增量同步要维护"目录消失/改名/UP 主改名"三类状态，收益不值那个复杂度。
/// </para>
/// </summary>
public sealed class CollectionRebuilder(LibraryDatabase db)
{
    /// <summary>文件夹合集的 id 前缀（源级）。</summary>
    public const string FolderPrefix = "dir:";

    /// <summary>UP 主合集的 id 前缀（源级）。</summary>
    public const string UpPrefix = "up:";

    /// <summary>手动合集的 id 前缀（源级）。meta override 引用时按需建行，不来自物理结构。</summary>
    public const string CustomPrefix = "custom:";

    /// <summary>没有 owner 信息时的 mid 兜底。</summary>
    public const string UnknownUp = "unknown";

    /// <summary>源根目录散落视频的合集 id。</summary>
    public static string RootFolderId(string sourceId) => $"{FolderPrefix}{sourceId}:/";

    /// <summary>某个相对目录的合集 id。<paramref name="relDir"/> 无前导斜杠，空串 = 源根。</summary>
    public static string FolderId(string sourceId, string relDir) =>
        $"{FolderPrefix}{sourceId}:/{relDir}";

    public static string UpId(string sourceId, string mid) => $"{UpPrefix}{sourceId}:{mid}";

    public sealed record Result(int Collections, int Bound, int Removed, int Overridden);

    public Result Rebuild()
    {
        var sourceNames = new SourceRepository(db).List()
            .ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);

        var folders = ReadFolders();
        var ups = ReadUps();

        using var tx = db.Connection.BeginTransaction();

        var removed = Exec("DELETE FROM collection;", tx);

        var insert = db.Connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT OR REPLACE INTO collection (id, name, parent_id, sort_order, source_id, kind, path)
            VALUES ($id, $name, NULL, $sort, $sid, $kind, $path);
            """;
        var pId = insert.Parameters.Add("$id", SqliteType.Text);
        var pName = insert.Parameters.Add("$name", SqliteType.Text);
        var pSort = insert.Parameters.Add("$sort", SqliteType.Integer);
        var pSid = insert.Parameters.Add("$sid", SqliteType.Text);
        var pKind = insert.Parameters.Add("$kind", SqliteType.Text);
        var pPath = insert.Parameters.Add("$path", SqliteType.Text);

        var sort = 0;
        foreach (var f in folders)
        {
            pId.Value = FolderId(f.SourceId, f.RelDir);
            pName.Value = FolderDisplayName(f.RelDir, sourceNames.GetValueOrDefault(f.SourceId) ?? f.SourceId);
            pSort.Value = sort++;
            pSid.Value = f.SourceId;
            pKind.Value = "folder";
            pPath.Value = f.RelDir;
            insert.ExecuteNonQuery();
        }

        foreach (var u in ups)
        {
            pId.Value = UpId(u.SourceId, u.Mid);
            pName.Value = u.UpName ?? "未识别 UP 主";
            pSort.Value = sort++;
            pSid.Value = u.SourceId;
            pKind.Value = "up";
            pPath.Value = u.Mid;
            insert.ExecuteNonQuery();
        }

        // 手动合集：media_meta 里被 override 引用的 custom 键按需建行。
        // 不来自物理结构，所以每次重建后必须从引用重新派生——
        // 没有任何条目引用的 custom 合集会消失（空的也留不住），这是设计行为。
        // 判"被引用"要求条目**还有位置行**：清理缺失后档案会留成无主锚点，
        // 那种档案不该继续撑出一个空合集（文件回来时重建会重新派生出它）。
        var customInsert = db.Connection.CreateCommand();
        customInsert.Transaction = tx;
        customInsert.CommandText =
            """
            INSERT OR REPLACE INTO collection (id, name, parent_id, sort_order, source_id, kind, path)
            SELECT DISTINCT
                'custom:' || mt.source_id || ':' || substr(mt.collection, 8),
                substr(mt.collection, 8), NULL, 5000, mt.source_id, 'custom', substr(mt.collection, 8)
            FROM media_meta mt
            WHERE mt.collection LIKE 'custom:%'
              AND EXISTS (
                  SELECT 1 FROM media m
                  WHERE m.source_id = mt.source_id AND m.key = mt.key);
            """;
        customInsert.ExecuteNonQuery();

        // 归属回填：与上面的 id 规则必须逐字一致，否则合集行与条目挂不上。
        var bound = Exec(
            $"""
             UPDATE media SET collection_id =
                 CASE layout
                     WHEN 'dual' THEN '{UpPrefix}' || source_id || ':' || COALESCE(NULLIF(up_mid, ''), '{UnknownUp}')
                     ELSE '{FolderPrefix}' || source_id || ':/' || COALESCE(rel_dir, '')
                 END
             WHERE layout IN ('single', 'dual');
             """, tx);

        // override 段：条目卡片上写了归属（media_meta.collection）且解析出的合集
        // 真实存在 → 覆盖投影；否则保持文件夹/UP 投影。键是去源化的，这里拼上
        // 条目当前所在的 sourceId——删源重加、跨源移动都能对上。
        // （先查后逐行更：override 是用户手动的少量数据，不值得写 UPDATE-FROM 玩花活。）
        var overridden = 0;
        var readOverrides = db.Connection.CreateCommand();
        readOverrides.Transaction = tx;
        readOverrides.CommandText =
            """
            SELECT m.source_id, m.key, mt.collection
            FROM media m
            JOIN media_meta mt ON mt.source_id = m.source_id AND mt.key = m.key
            WHERE mt.collection IS NOT NULL AND mt.collection <> '';
            """;
        var (ovSource, ovKey, ovStored) = (new List<string>(), new List<string>(), new List<string>());
        using (var reader = readOverrides.ExecuteReader())
        {
            while (reader.Read())
            {
                ovSource.Add(reader.GetString(0));
                ovKey.Add(reader.GetString(1));
                ovStored.Add(reader.GetString(2));
            }
        }

        for (var i = 0; i < ovSource.Count; i++)
        {
            var resolved = ResolveOverrideId(ovSource[i], ovStored[i]);
            if (resolved is null) continue;

            var bind = db.Connection.CreateCommand();
            bind.Transaction = tx;
            bind.CommandText =
                """
                UPDATE media SET collection_id = $cid
                WHERE source_id = $sid AND key = $key
                  AND EXISTS (SELECT 1 FROM collection c WHERE c.id = $cid);
                """;
            bind.Parameters.AddWithValue("$cid", resolved);
            bind.Parameters.AddWithValue("$sid", ovSource[i]);
            bind.Parameters.AddWithValue("$key", ovKey[i]);
            overridden += bind.ExecuteNonQuery();
        }

        tx.Commit();
        return new Result(folders.Count + ups.Count, bound, removed, overridden);
    }

    /// <summary>去源化 override 键 → 本源的真实合集 id。未知前缀返回 null（忽略）。</summary>
    private static string? ResolveOverrideId(string sourceId, string stored)
    {
        if (stored.StartsWith(FolderPrefix, StringComparison.Ordinal))
        {
            return $"{FolderPrefix}{sourceId}:{stored[FolderPrefix.Length..]}";
        }

        if (stored.StartsWith(UpPrefix, StringComparison.Ordinal))
        {
            return $"{UpPrefix}{sourceId}:{stored[UpPrefix.Length..]}";
        }

        if (stored.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            return $"{CustomPrefix}{sourceId}:{stored[CustomPrefix.Length..]}";
        }

        return null;
    }

    /// <summary>
    /// 合集归属 override 的即时落位（"移动到合集"接口用）。media_meta 的写入
    /// 由 <c>MetaRepository.Apply</c> 完成，这里把 <c>media.collection_id</c> 也同步掉，
    /// 调用方不用等下次扫描：
    /// <list type="bullet">
    /// <item><c>custom:*</c> → 按需建合集行（与 <see cref="Rebuild"/> 的引用派生一致）；</item>
    /// <item><c>dir:/up:</c> → 目标合集存在才落位，不存在则 override 留到投影出现那天；</item>
    /// <item>空串 → 清除 override，恢复目录/UP 投影归属。</item>
    /// </list>
    /// </summary>
    public void ApplyOverride(string sourceId, IReadOnlyList<string> keys, string storedKey)
    {
        if (keys.Count == 0) return;

        var paramNames = keys.Select((_, i) => $"$k{i}").ToArray();

        if (storedKey.Length == 0)
        {
            // 恢复投影：与 Rebuild 的回填同一条公式
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText =
                $"""
                 UPDATE media SET collection_id =
                     CASE layout
                         WHEN 'dual' THEN '{UpPrefix}' || source_id || ':' || COALESCE(NULLIF(up_mid, ''), '{UnknownUp}')
                         ELSE '{FolderPrefix}' || source_id || ':/' || COALESCE(rel_dir, '')
                     END
                 WHERE source_id = $sid AND key IN ({string.Join(", ", paramNames)});
                 """;
            cmd.Parameters.AddWithValue("$sid", sourceId);
            for (var i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue(paramNames[i], keys[i]);
            cmd.ExecuteNonQuery();
            return;
        }

        var resolved = storedKey switch
        {
            var k when k.StartsWith(FolderPrefix) => $"{FolderPrefix}{sourceId}:{k[FolderPrefix.Length..]}",
            var k when k.StartsWith(UpPrefix) => $"{UpPrefix}{sourceId}:{k[UpPrefix.Length..]}",
            var k when k.StartsWith(CustomPrefix) => $"{CustomPrefix}{sourceId}:{k[CustomPrefix.Length..]}",
            _ => null,
        };
        if (resolved is null) return;

        // custom 合集按需建行（无引用即消失的语义与重建一致，这里先建上）
        if (storedKey.StartsWith(CustomPrefix))
        {
            var name = storedKey[CustomPrefix.Length..];
            using var ci = db.Connection.CreateCommand();
            ci.CommandText =
                """
                INSERT OR REPLACE INTO collection (id, name, parent_id, sort_order, source_id, kind, path)
                VALUES ($id, $name, NULL, 5000, $sid, 'custom', $name);
                """;
            ci.Parameters.AddWithValue("$id", resolved);
            ci.Parameters.AddWithValue("$name", name);
            ci.Parameters.AddWithValue("$sid", sourceId);
            ci.ExecuteNonQuery();
        }

        using var up = db.Connection.CreateCommand();
        up.CommandText =
            $"""
             UPDATE media SET collection_id = $cid
             WHERE source_id = $sid AND key IN ({string.Join(", ", paramNames)})
               AND EXISTS (SELECT 1 FROM collection c WHERE c.id = $cid);
             """;
        up.Parameters.AddWithValue("$cid", resolved);
        up.Parameters.AddWithValue("$sid", sourceId);
        for (var i = 0; i < keys.Count; i++) up.Parameters.AddWithValue(paramNames[i], keys[i]);
        up.ExecuteNonQuery();
    }

    /// <summary>合集显示名：取目录末段（"科幻/2024" → "2024"）；源根用源名，多源聚合时能分清。 */
    internal static string FolderDisplayName(string relDir, string sourceName)
    {
        if (string.IsNullOrEmpty(relDir)) return sourceName;
        var cut = relDir.LastIndexOf('/');
        return cut < 0 ? relDir : relDir[(cut + 1)..];
    }

    private sealed record FolderGroup(string SourceId, string RelDir, int Count);

    private sealed record UpGroup(string SourceId, string Mid, string? UpName, int Count);

    private List<FolderGroup> ReadFolders()
    {
        var list = new List<FolderGroup>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT source_id, COALESCE(rel_dir, ''), COUNT(*)
            FROM media
            WHERE layout = 'single'
            GROUP BY source_id, COALESCE(rel_dir, '')
            ORDER BY source_id, COALESCE(rel_dir, '');
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FolderGroup(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return list;
    }

    private List<UpGroup> ReadUps()
    {
        var list = new List<UpGroup>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            $"""
             SELECT source_id,
                    COALESCE(NULLIF(up_mid, ''), '{UnknownUp}'),
                    MAX(NULLIF(up_name, '')),
                    COUNT(*)
             FROM media
             WHERE layout = 'dual'
             GROUP BY source_id, COALESCE(NULLIF(up_mid, ''), '{UnknownUp}')
             ORDER BY MAX(NULLIF(up_name, '')) COLLATE NOCASE, source_id;
             """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new UpGroup(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3)));
        }

        return list;
    }

    private int Exec(string sql, SqliteTransaction tx)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }
}
