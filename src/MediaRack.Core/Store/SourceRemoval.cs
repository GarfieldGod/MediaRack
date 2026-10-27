using Microsoft.Data.Sqlite;

namespace MediaRack.Store;

/// <summary>
/// 删除一个源（管理页的「删除源」）。<b>磁盘文件一行不动</b>，动的是库里的登记信息。
/// <para>
/// 分两层，与「清理缺失」同一套口径：
/// </para>
/// <list type="number">
/// <item><b>位置层</b>（总是删）：源登记、文件夹登记、该源全部条目
/// （<c>media</c> / <c>media_track</c>）、该源的合集投影行。</item>
/// <item><b>档案层</b>（默认保留，可选删）：<c>media_meta</c>（旋转/进度/标题/标签/合集归属）
/// 与 <c>favorite_item</c>（收藏）。它们的键是 <c>(source_id, key)</c>，位置删了也不失效——
/// 文件搬回原处时 key 不变，档案自动生效；换路径搬回来则由 <c>content_fp</c> 认领。
/// 所以"删源"不等于"删设置"。</item>
/// </list>
/// <para>
/// 不删档案的代价是库里留下无主的行。这是有意的：这些行就是"下次还能认回来"的唯一凭据。
/// 真要彻底抹掉，调用方传 <c>withArchive: true</c>。
/// </para>
/// </summary>
public sealed class SourceRemoval(LibraryDatabase db)
{
    /// <param name="SourceId">被删的源。</param>
    /// <param name="MediaRemoved">删掉的条目行数（等于该源删前的全部条目，含 missing）。</param>
    /// <param name="FoldersRemoved">删掉的文件夹登记数。</param>
    /// <param name="CollectionsRemoved">删掉的合集投影行数。</param>
    /// <param name="MetaRemoved">一并删掉的档案份数（标题/标签/旋转/进度/合集归属）。</param>
    /// <param name="FavoriteItemsRemoved">一并删掉的收藏引用条数。</param>
    /// <param name="MetaKept">删完后仍留在库里、属于这个源的档案份数（没勾"连档案一起删"时＝全部）。</param>
    /// <param name="FavoriteItemsKept">同上，收藏引用条数。</param>
    /// <param name="FingerprintsRemoved">收尾清掉的、已无处可指的指纹账行数。</param>
    public sealed record Result(
        string SourceId,
        int MediaRemoved,
        int FoldersRemoved,
        int CollectionsRemoved,
        int MetaRemoved,
        int FavoriteItemsRemoved,
        int MetaKept,
        int FavoriteItemsKept,
        int FingerprintsRemoved);

    /// <returns>源不存在时返回 <c>null</c>（调用方报 404）。</returns>
    public Result? Run(string sourceId, bool withArchive = false)
    {
        using var tx = db.Connection.BeginTransaction();

        if (!Exists(tx, sourceId))
        {
            tx.Rollback();
            return null;
        }

        Run(tx, "DELETE FROM media_track WHERE source_id = $s;", sourceId);
        var media = Run(tx, "DELETE FROM media WHERE source_id = $s;", sourceId);

        // 收藏的合集（favorite_collection）引用合集 id：合集行要走，引用一起走，免得侧栏留空壳。
        Run(tx,
            """
            DELETE FROM favorite_collection
            WHERE collection_id IN (SELECT id FROM collection WHERE source_id = $s);
            """, sourceId);
        var collections = Run(tx, "DELETE FROM collection WHERE source_id = $s;", sourceId);

        var folders = Run(tx, "DELETE FROM source_folder WHERE source_id = $s;", sourceId);

        // 「继续观看」是单行指针：指向的条目已经没了就清空指针（行本身留着）。
        Run(tx, "UPDATE last_session SET source_id = NULL, key = NULL, pos_ms = NULL WHERE source_id = $s;",
            sourceId);

        Run(tx, "DELETE FROM source WHERE id = $s;", sourceId);

        var meta = withArchive ? Run(tx, "DELETE FROM media_meta WHERE source_id = $s;", sourceId) : 0;
        var favorites = withArchive ? Run(tx, "DELETE FROM favorite_item WHERE source_id = $s;", sourceId) : 0;

        // 留在库里的档案：没勾"连档案一起删"时就是全部，勾了就是 0。
        var metaKept = Count(tx, "SELECT COUNT(*) FROM media_meta WHERE source_id = $s;", sourceId);
        var favoritesKept = Count(tx, "SELECT COUNT(*) FROM favorite_item WHERE source_id = $s;", sourceId);

        // 指纹账收尾：只清"既没有位置行、也没有档案、也没有收藏"的账。
        // 指得到档案的账必须留着——它是"换了路径/换了源标识也能认回设置"的唯一路标。
        // 顺带收掉全库早已空掉的账（之前删过位置、档案也一并没了的那种），代价是一次全表扫。
        var fingerprints = Run(tx,
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
            """);

        tx.Commit();
        return new Result(sourceId, media, folders, collections, meta, favorites, metaKept, favoritesKept, fingerprints);
    }

    private static int Count(SqliteTransaction tx, string sql, string sourceId)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", sourceId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int Run(SqliteTransaction tx, string sql, string? sourceId = null)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (sourceId is not null) cmd.Parameters.AddWithValue("$s", sourceId);
        return cmd.ExecuteNonQuery();
    }

    private bool Exists(SqliteTransaction tx, string sourceId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM source WHERE id = $s LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", sourceId);
        return cmd.ExecuteScalar() is not null;
    }
}
