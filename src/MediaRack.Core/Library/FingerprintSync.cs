using MediaRack.Media;
using MediaRack.Models;
using MediaRack.Store;
using MediaRack.Virtual;

namespace MediaRack.Library;

/// <summary>
/// 内容指纹认领：single 条目在扫描入库后，按内容指纹到全局台账
/// <c>content_fp</c> 里找"这份内容上次出现在哪"。找到了就把旧位置的
/// meta（旋转/隐藏/标签/标题覆盖/播放进度）认领到新位置。
/// <para>
/// 只复制、永不删除：旧位置的 media 行是 ghost 还是"同内容第二份"，
/// 扫描器分不清也不需要分清——ghost 由各源自己的 MarkMissingExcept 流程
/// 收拾，第二份则按用户拍板的语义共享同一套设置。
/// </para>
/// <para>
/// 台账按 fp 只记最近位置（INSERT OR REPLACE）：绑定的意义是"指路"，
/// 不是归属。已登记过 (source,key) 且大小没变的条目直接跳过，重复扫描
/// 不反复读文件头尾。
/// </para>
/// </summary>
public static class FingerprintSync
{
    public static int Sync(
        LibraryDatabase db,
        IReadOnlyList<MediaEntry> singleEntries,
        IReadOnlyDictionary<string, string> filePathByEntryKey)
    {
        if (singleEntries.Count == 0) return 0;

        var meta = new MetaRepository(db);
        var migrated = 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var entry in singleEntries)
        {
            // 0 字节/读不出元数据的文件不值得认；指纹读不出的（IO 错误）跳过下次再算
            if (entry.Status != MediaStatus.Ok) continue;
            // map 里存的是条目所在文件夹的根，拼上相对路径才是文件本体
            if (!filePathByEntryKey.TryGetValue(entry.Key, out var root)) continue;
            var path = Path.Combine(root, entry.RelPath);
            if (!File.Exists(path)) continue;

            // 已登记过且大小没变：内容没动，不重算指纹
            var knownSize = LedgerSize(db, entry.SourceId, entry.Key);
            if (knownSize is { } size && size == (entry.SizeBytes ?? 0)) continue;

            string fp;
            try
            {
                fp = ContentFingerprint.Compute(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var previous = Locate(db, fp);
            if (previous is not null &&
                !(previous.Value.SourceId == entry.SourceId && previous.Value.Key == entry.Key))
            {
                if (meta.CopyMeta(previous.Value.SourceId, previous.Value.Key,
                        entry.SourceId, entry.Key, "fp-migrate"))
                {
                    migrated++;
                }

                // 收藏搬家（与 meta 同一趟指纹查询，零额外 IO）：
                // 旧位置收藏过的夹子都给新位置补上（OR IGNORE 去重，两份共存时两边都有——
                // 与 meta 的"共享"语义一致）；旧条目已被标 missing 的，它的收藏记录放掉。
                ClaimFavorites(db, previous.Value.SourceId, previous.Value.Key,
                    entry.SourceId, entry.Key);
                ReleaseMissingFavorites(db, previous.Value.SourceId, previous.Value.Key);
            }

            Bind(db, fp, entry.SourceId, entry.Key, entry.SizeBytes ?? 0, now);
        }

        return migrated;
    }

    private static long? LedgerSize(LibraryDatabase db, string sourceId, string key)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT size_bytes FROM content_fp WHERE source_id = $sid AND key = $key;";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$key", key);
        var result = cmd.ExecuteScalar();
        return result is long bytes ? bytes : null;
    }

    /// <summary>
    /// 内容指纹在台账里目前指向哪个位置。<c>null</c> = 这份内容没登记过。
    /// 对外公开是为了能验证"删源/清理之后账还在"这类断言——台账是认领的唯一路标，
    /// 它丢了，档案就永远认不回来了。
    /// </summary>
    public static (string SourceId, string Key)? Locate(LibraryDatabase db, string fp)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT source_id, key FROM content_fp WHERE fp = $fp;";
        cmd.Parameters.AddWithValue("$fp", fp);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static void Bind(LibraryDatabase db, string fp, string sourceId, string key, long size, long now)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO content_fp (fp, source_id, key, size_bytes, updated_at)
            VALUES ($fp, $sid, $key, $size, $now)
            ON CONFLICT(fp) DO UPDATE SET
                source_id  = excluded.source_id,
                key        = excluded.key,
                size_bytes = excluded.size_bytes,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$fp", fp);
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把旧位置的收藏记录补到新位置（加入时间原样保留，收藏夹排序不变）。
    /// 主键去重：新位置已在同一夹子里就跳过。只加不减——旧条目还活着
    /// （同内容两份共存）时两边都有收藏，匹配 meta 的共享语义。
    /// </summary>
    private static void ClaimFavorites(
        LibraryDatabase db, string fromSource, string fromKey, string toSource, string toKey)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR IGNORE INTO favorite_item (favorite_id, source_id, key, added_at)
            SELECT favorite_id, $toSid, $toKey, added_at
            FROM favorite_item
            WHERE source_id = $fromSid AND key = $fromKey;
            """;
        cmd.Parameters.AddWithValue("$fromSid", fromSource);
        cmd.Parameters.AddWithValue("$fromKey", fromKey);
        cmd.Parameters.AddWithValue("$toSid", toSource);
        cmd.Parameters.AddWithValue("$toKey", toKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 旧条目已被标 missing 时放掉它名下的收藏（真正的"搬家"）；
    /// 条目还在（corrupt/ok）就留着，交给用户自己处理。
    /// </summary>
    private static void ReleaseMissingFavorites(LibraryDatabase db, string sourceId, string key)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM favorite_item
            WHERE source_id = $sid AND key = $key
              AND EXISTS (
                  SELECT 1 FROM media m
                  WHERE m.source_id = favorite_item.source_id AND m.key = favorite_item.key
                    AND m.status = 'missing');
            """;
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }
}
