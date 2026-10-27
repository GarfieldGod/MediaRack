using Microsoft.Data.Sqlite;

namespace MediaRack.Store;

/// <summary>
/// 手动清理 missing 条目。<b>扫描永远不会调用这里</b>——只有用户在管理页
/// 显式点「清理缺失」才走这条路径。
/// <para>
/// 语义（用户拍板）：<b>位置记录全删，档案一律保留</b>。删掉的是"某个源在某个路径
/// 曾经有过一条视频"这件事；<c>media_meta</c>（旋转/进度/标签/标题覆盖/合集归属）
/// 与 <c>favorite_item</c>（收藏）<b>一行都不动</b>，指纹台账里还指得到档案的行也不动。
/// </para>
/// <para>
/// 为什么删了位置还能恢复：single 的 key 是<b>相对路径哈希</b>，文件搬回原处时 key
/// 不变，档案按 (source_id, key) 自动生效，连指纹都不需要；搬到别处则由
/// <c>content_fp</c> 里那条"指回旧 key"的账把档案认领过去。所以留账、留档案、
/// 删位置，三件事互不冲突。
/// </para>
/// <para>
/// 唯一会清掉的账：指向的位置既没档案也没收藏——那条账已经无处可指、
/// 也再没有东西可以认领。
/// </para>
/// </summary>
public sealed class MissingPurge(LibraryDatabase db)
{
    /// <param name="Deleted">删掉的位置记录条数。</param>
    /// <param name="AnchorsKept">删完之后仍然留着的"无主档案"份数（等文件回来认领）。</param>
    /// <param name="OrphanFingerprintsRemoved">一并清掉的、已无处可指的指纹账行数。</param>
    public sealed record Result(int Deleted, int AnchorsKept, int OrphanFingerprintsRemoved);

    public Result Run(string? sourceId = null)
    {
        var scope = sourceId is null ? "" : " AND m.source_id = $s";

        using var tx = db.Connection.BeginTransaction();

        // 目标集合先落临时表：三条 DELETE 共用，也避免边删边查。
        using (var create = db.Connection.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS purge_keys (source_id TEXT, key TEXT);";
            create.ExecuteNonQuery();
        }

        using (var clear = db.Connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM purge_keys;";
            clear.ExecuteNonQuery();
        }

        int targets;
        using (var collect = db.Connection.CreateCommand())
        {
            collect.Transaction = tx;
            collect.CommandText =
                $"""
                 INSERT INTO purge_keys (source_id, key)
                 SELECT m.source_id, m.key FROM media m
                 WHERE m.status = 'missing'{scope};
                 """;
            if (sourceId is not null) collect.Parameters.AddWithValue("$s", sourceId);
            targets = collect.ExecuteNonQuery();
        }

        if (targets == 0)
        {
            tx.Commit();
            return new Result(0, 0, 0);
        }

        // 分轨先删（media 上没有级联），再删位置行本身。
        using (var delTrack = db.Connection.CreateCommand())
        {
            delTrack.Transaction = tx;
            delTrack.CommandText =
                """
                DELETE FROM media_track
                WHERE EXISTS (
                    SELECT 1 FROM purge_keys p
                    WHERE p.source_id = media_track.source_id AND p.key = media_track.key);
                """;
            delTrack.ExecuteNonQuery();
        }

        int deleted;
        using (var delMedia = db.Connection.CreateCommand())
        {
            delMedia.Transaction = tx;
            delMedia.CommandText =
                """
                DELETE FROM media
                WHERE status = 'missing'
                  AND EXISTS (
                      SELECT 1 FROM purge_keys p
                      WHERE p.source_id = media.source_id AND p.key = media.key);
                """;
            deleted = delMedia.ExecuteNonQuery();
        }

        // 指纹账收尾：只清"既没有位置行、也没有档案、也没有收藏"的账。
        // 指得到档案的账必须留着——它是"搬到别处也能认回设置"的唯一路标。
        int orphanFp;
        using (var fp = db.Connection.CreateCommand())
        {
            fp.Transaction = tx;
            fp.CommandText =
                """
                DELETE FROM content_fp
                WHERE NOT EXISTS (
                          SELECT 1 FROM media m
                          WHERE m.source_id = content_fp.source_id AND m.key = content_fp.key)
                  AND NOT EXISTS (
                          SELECT 1 FROM media_meta t
                          WHERE t.source_id = content_fp.source_id AND t.key = content_fp.key)
                  AND NOT EXISTS (
                          SELECT 1 FROM favorite_item f
                          WHERE f.source_id = content_fp.source_id AND f.key = content_fp.key);
                """;
            orphanFp = fp.ExecuteNonQuery();
        }

        // 报告口径：现在还留着多少个"没有位置、但有档案"的锚点（等文件回来）。
        int anchors;
        using (var count = db.Connection.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText =
                $"""
                 SELECT COUNT(*) FROM (
                     SELECT t.source_id, t.key FROM media_meta t
                     UNION
                     SELECT f.source_id, f.key FROM favorite_item f
                 ) a
                 WHERE NOT EXISTS (
                     SELECT 1 FROM media m
                     WHERE m.source_id = a.source_id AND m.key = a.key)
                 {(sourceId is null ? "" : "AND a.source_id = $s")};
                 """;
            if (sourceId is not null) count.Parameters.AddWithValue("$s", sourceId);
            anchors = Convert.ToInt32(count.ExecuteScalar());
        }

        tx.Commit();
        return new Result(deleted, anchors, orphanFp);
    }
}
