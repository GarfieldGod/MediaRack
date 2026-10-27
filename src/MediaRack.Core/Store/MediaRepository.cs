using System.Text.Json;
using MediaRack.Models;
using Microsoft.Data.Sqlite;

namespace MediaRack.Store;

/// <summary>条目的读写。只碰库，绝不碰源目录。</summary>
public sealed class MediaRepository(LibraryDatabase db)
{
    private const string UpsertSql =
        """
        INSERT INTO media (
            source_id, key, rel_path, rel_dir, layout, title_raw, up_name, up_mid,
            duration_ms, width, height, codec, tag_code, size_bytes, poster_rel,
            has_danmaku, status, scanned_at, file_mtime)
        VALUES (
            $sid, $key, $relPath, $relDir, $layout, $title, $upName, $upMid,
            $dur, $w, $h, $codec, $tag, $size, $poster,
            $danmaku, $status, $scanned, $mtime)
        ON CONFLICT(source_id, key) DO UPDATE SET
            rel_path    = excluded.rel_path,
            rel_dir     = excluded.rel_dir,
            layout      = excluded.layout,
            title_raw   = COALESCE(excluded.title_raw, media.title_raw),
            up_name     = COALESCE(excluded.up_name, media.up_name),
            up_mid      = COALESCE(excluded.up_mid, media.up_mid),
            duration_ms = COALESCE(excluded.duration_ms, media.duration_ms),
            width       = COALESCE(excluded.width, media.width),
            height      = COALESCE(excluded.height, media.height),
            codec       = COALESCE(excluded.codec, media.codec),
            tag_code    = COALESCE(excluded.tag_code, media.tag_code),
            size_bytes  = COALESCE(excluded.size_bytes, media.size_bytes),
            poster_rel  = COALESCE(excluded.poster_rel, media.poster_rel),
            has_danmaku = excluded.has_danmaku,
            status      = excluded.status,
            scanned_at  = excluded.scanned_at,
            file_mtime  = COALESCE(excluded.file_mtime, media.file_mtime);
        """;

    /// <summary>
    /// 批量写入条目（含分轨）。整体一个事务：中途失败不会留下半条记录。
    /// 返回写入的条目数。
    /// </summary>
    public int Upsert(string sourceId, IReadOnlyList<MediaEntry> entries)
    {
        if (entries.Count == 0) return 0;

        using var tx = db.Connection.BeginTransaction();

        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = UpsertSql;

        var pSid = cmd.Parameters.Add("$sid", SqliteType.Text);
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pRelPath = cmd.Parameters.Add("$relPath", SqliteType.Text);
        var pRelDir = cmd.Parameters.Add("$relDir", SqliteType.Text);
        var pLayout = cmd.Parameters.Add("$layout", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pUpName = cmd.Parameters.Add("$upName", SqliteType.Text);
        var pUpMid = cmd.Parameters.Add("$upMid", SqliteType.Text);
        var pDur = cmd.Parameters.Add("$dur", SqliteType.Integer);
        var pW = cmd.Parameters.Add("$w", SqliteType.Integer);
        var pH = cmd.Parameters.Add("$h", SqliteType.Integer);
        var pCodec = cmd.Parameters.Add("$codec", SqliteType.Text);
        var pTag = cmd.Parameters.Add("$tag", SqliteType.Integer);
        var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
        var pPoster = cmd.Parameters.Add("$poster", SqliteType.Text);
        var pDanmaku = cmd.Parameters.Add("$danmaku", SqliteType.Integer);
        var pStatus = cmd.Parameters.Add("$status", SqliteType.Text);
        var pScanned = cmd.Parameters.Add("$scanned", SqliteType.Integer);
        var pMtime = cmd.Parameters.Add("$mtime", SqliteType.Integer);

        using var delTracks = db.Connection.CreateCommand();
        delTracks.Transaction = tx;
        delTracks.CommandText = "DELETE FROM media_track WHERE source_id = $sid AND key = $key;";
        var dSid = delTracks.Parameters.Add("$sid", SqliteType.Text);
        var dKey = delTracks.Parameters.Add("$key", SqliteType.Text);

        using var insTrack = db.Connection.CreateCommand();
        insTrack.Transaction = tx;
        insTrack.CommandText =
            """
            INSERT INTO media_track (source_id, key, role, codecid, bandwidth, size_bytes, md5)
            VALUES ($sid, $key, $role, $codecid, $bw, $size, $md5);
            """;
        var tSid = insTrack.Parameters.Add("$sid", SqliteType.Text);
        var tKey = insTrack.Parameters.Add("$key", SqliteType.Text);
        var tRole = insTrack.Parameters.Add("$role", SqliteType.Text);
        var tCodec = insTrack.Parameters.Add("$codecid", SqliteType.Integer);
        var tBw = insTrack.Parameters.Add("$bw", SqliteType.Integer);
        var tSize = insTrack.Parameters.Add("$size", SqliteType.Integer);
        var tMd5 = insTrack.Parameters.Add("$md5", SqliteType.Text);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var written = 0;

        foreach (var e in entries)
        {
            pSid.Value = sourceId;
            pKey.Value = e.Key;
            pRelPath.Value = e.RelPath;
            pRelDir.Value = (object?)e.RelDir ?? DBNull.Value;
            pLayout.Value = LayoutToDb(e.Layout);
            pTitle.Value = (object?)e.TitleRaw ?? DBNull.Value;
            pUpName.Value = (object?)e.UpName ?? DBNull.Value;
            pUpMid.Value = (object?)e.UpMid ?? DBNull.Value;
            pDur.Value = (object?)e.DurationMs ?? DBNull.Value;
            pW.Value = (object?)e.Width ?? DBNull.Value;
            pH.Value = (object?)e.Height ?? DBNull.Value;
            pCodec.Value = (object?)e.Codec ?? DBNull.Value;
            pTag.Value = (object?)e.TagCode ?? DBNull.Value;
            pSize.Value = (object?)e.SizeBytes ?? DBNull.Value;
            pPoster.Value = (object?)e.PosterRel ?? DBNull.Value;
            pDanmaku.Value = e.HasDanmaku ? 1 : 0;
            pStatus.Value = StatusToDb(e.Status);
            pScanned.Value = e.ScannedAt ?? now;
            pMtime.Value = (object?)e.FileMtime ?? DBNull.Value;
            cmd.ExecuteNonQuery();

            dSid.Value = sourceId;
            dKey.Value = e.Key;
            delTracks.ExecuteNonQuery();

            foreach (var t in e.Tracks)
            {
                tSid.Value = sourceId;
                tKey.Value = e.Key;
                tRole.Value = t.Role == TrackRole.Audio ? "audio" : "video";
                tCodec.Value = (object?)t.CodecId ?? DBNull.Value;
                tBw.Value = (object?)t.Bandwidth ?? DBNull.Value;
                tSize.Value = (object?)t.SizeBytes ?? DBNull.Value;
                tMd5.Value = (object?)t.Md5 ?? DBNull.Value;
                insTrack.ExecuteNonQuery();
            }

            written++;
        }

        tx.Commit();
        return written;
    }

    /// <summary>
    /// 把本次扫描<b>没出现</b>的既有条目标记为 missing（而不是删除）。
    /// 条目突然消失通常是移动硬盘没插、路径变了，不该让用户以为库被清空。
    /// </summary>
    public int MarkMissingExcept(string sourceId, IReadOnlyCollection<string> seenKeys)
    {
        using var tx = db.Connection.BeginTransaction();

        using (var create = db.Connection.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS scan_keys (key TEXT PRIMARY KEY);";
            create.ExecuteNonQuery();
        }

        using (var clear = db.Connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM scan_keys;";
            clear.ExecuteNonQuery();
        }

        using (var insert = db.Connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO scan_keys(key) VALUES ($k);";
            var p = insert.Parameters.Add("$k", SqliteType.Text);
            foreach (var k in seenKeys)
            {
                p.Value = k;
                insert.ExecuteNonQuery();
            }
        }

        int affected;
        using (var update = db.Connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE media SET status = 'missing'
                WHERE source_id = $sid
                  AND status <> 'missing'
                  AND key NOT IN (SELECT key FROM scan_keys);
                """;
            update.Parameters.AddWithValue("$sid", sourceId);
            affected = update.ExecuteNonQuery();
        }

        tx.Commit();
        return affected;
    }

    private static string LayoutToDb(SourceLayout layout) =>
        layout == SourceLayout.Dual ? "dual" : "single";

    // ── 缩略图 ──────────────────────────────────────────────

    /// <summary>
    /// 抽帧任务队列。只读：返回"需要生成缩略图"的候选，由 <c>PosterGenerator</c> 处理，
    /// 结果再经 <see cref="UpdatePosters"/> 写回。
    /// </summary>
    /// <param name="sourceId">源标识。</param>
    /// <param name="onlyMissing">true 只返回 <c>poster_rel IS NULL</c> 的条目。</param>
    public IReadOnlyList<PosterCandidate> ListPosterCandidates(string sourceId, bool onlyMissing = true)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT key, rel_path, layout, tag_code, poster_rel, duration_ms
            FROM media
            WHERE source_id = $sid
              AND status = 'ok'
              AND ($onlyMissing = 0 OR poster_rel IS NULL)
            ORDER BY key;
            """;
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$onlyMissing", onlyMissing ? 1 : 0);

        var list = new List<PosterCandidate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PosterCandidate(
                Key: reader.GetString(0),
                RelPath: reader.GetString(1),
                Layout: reader.GetString(2) == "dual" ? SourceLayout.Dual : SourceLayout.Single,
                TagCode: reader.IsDBNull(3) ? null : (int)reader.GetInt64(3),
                PosterRel: reader.IsDBNull(4) ? null : reader.GetString(4),
                DurationMs: reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return list;
    }

    /// <summary>批量回写 <c>poster_rel</c>。整体一个事务。</summary>
    public int UpdatePosters(string sourceId, IReadOnlyList<(string Key, string PosterRel)> items)
    {
        if (items.Count == 0) return 0;

        using var tx = db.Connection.BeginTransaction();
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE media SET poster_rel = $poster WHERE source_id = $sid AND key = $key;";

        var pPoster = cmd.Parameters.Add("$poster", SqliteType.Text);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Text);
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);

        var updated = 0;
        foreach (var (key, posterRel) in items)
        {
            pPoster.Value = posterRel;
            pSid.Value = sourceId;
            pKey.Value = key;
            updated += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return updated;
    }

    // ── 浏览 ────────────────────────────────────────────────

    /// <summary>
    /// 按源列出条目，供 <c>mrack ls</c> 与后续 Web 页使用。
    /// <para>
    /// 标题取 <c>COALESCE(title_display, title_raw)</c>——虚拟覆盖优先，没改过就跟随源端。
    /// 隐藏条目默认不返回（隐藏是视图层的事，磁盘上没有区别）。
    /// </para>
    /// </summary>
    public IReadOnlyList<MediaListRow> List(
        string? sourceId, int limit = 100, bool includeHidden = false)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT m.source_id, m.key,
                   COALESCE(mt.title_display, m.title_raw) AS title,
                   m.rel_path, m.rel_dir, m.width, m.height, m.duration_ms,
                   m.poster_rel, m.status, m.codec,
                   COALESCE(mt.hidden, 0)      AS hidden,
                   mt.title_display IS NOT NULL AS overridden
            FROM media m
            LEFT JOIN media_meta mt
                   ON mt.source_id = m.source_id AND mt.key = m.key
            WHERE ($sid IS NULL OR m.source_id = $sid)
              AND ($includeHidden = 1 OR COALESCE(mt.hidden, 0) = 0)
            ORDER BY m.source_id, m.rel_dir, title
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$sid", (object?)sourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$includeHidden", includeHidden ? 1 : 0);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<MediaListRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MediaListRow(
                SourceId: reader.GetString(0),
                Key: reader.GetString(1),
                Title: reader.IsDBNull(2) ? null : reader.GetString(2),
                RelPath: reader.GetString(3),
                RelDir: reader.IsDBNull(4) ? null : reader.GetString(4),
                Width: reader.IsDBNull(5) ? null : (int)reader.GetInt64(5),
                Height: reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
                DurationMs: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                PosterRel: reader.IsDBNull(8) ? null : reader.GetString(8),
                Status: reader.GetString(9),
                Codec: reader.IsDBNull(10) ? null : reader.GetString(10),
                Hidden: reader.GetInt64(11) != 0,
                TitleOverridden: reader.GetInt64(12) != 0));
        }

        return list;
    }

    /// <summary>
    /// 同一个 key 可能同时存在于多个源（这正是多源聚合的前提）。
    /// 返回持有该 key 的源 id，按 id 排序。只有一个时就是它，多个时必须由调用方指定。
    /// </summary>
    public IReadOnlyList<string> SourcesForKey(string key)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT source_id FROM media WHERE key = $key ORDER BY source_id;";
        cmd.Parameters.AddWithValue("$key", key);

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>
    /// 读一条完整条目（含分轨）。服务端的详情接口与流端点都靠它拿
    /// <c>rel_path</c> / <c>tag_code</c>，再用 <see cref="Media.MediaLocator"/> 还原绝对路径。
    /// </summary>
    public MediaEntry? Get(string sourceId, string key)
    {
        MediaEntry? entry = null;

        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT rel_path, rel_dir, layout, title_raw, up_name, up_mid,
                       duration_ms, width, height, codec, tag_code, size_bytes,
                       poster_rel, has_danmaku, status, scanned_at, collection_id,
                       file_mtime
                FROM media
                WHERE source_id = $sid AND key = $key;
                """;
            cmd.Parameters.AddWithValue("$sid", sourceId);
            cmd.Parameters.AddWithValue("$key", key);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                entry = new MediaEntry
                {
                    SourceId = sourceId,
                    Key = key,
                    RelPath = reader.GetString(0),
                    RelDir = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Layout = LayoutFromDb(reader.GetString(2)),
                    TitleRaw = reader.IsDBNull(3) ? null : reader.GetString(3),
                    UpName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    UpMid = reader.IsDBNull(5) ? null : reader.GetString(5),
                    DurationMs = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    Width = reader.IsDBNull(7) ? null : (int)reader.GetInt64(7),
                    Height = reader.IsDBNull(8) ? null : (int)reader.GetInt64(8),
                    Codec = reader.IsDBNull(9) ? null : reader.GetString(9),
                    TagCode = reader.IsDBNull(10) ? null : (int)reader.GetInt64(10),
                    SizeBytes = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    PosterRel = reader.IsDBNull(12) ? null : reader.GetString(12),
                    HasDanmaku = reader.GetInt64(13) != 0,
                    Status = StatusFromDb(reader.GetString(14)),
                    ScannedAt = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    CollectionId = reader.IsDBNull(16) ? null : reader.GetString(16),
                    FileMtime = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                };
            }
        }

        if (entry is null) return null;

        var tracks = new List<MediaTrack>();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT role, codecid, bandwidth, size_bytes, md5
                FROM media_track
                WHERE source_id = $sid AND key = $key
                ORDER BY role;
                """;
            cmd.Parameters.AddWithValue("$sid", sourceId);
            cmd.Parameters.AddWithValue("$key", key);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tracks.Add(new MediaTrack
                {
                    Role = reader.GetString(0) == "audio" ? TrackRole.Audio : TrackRole.Video,
                    CodecId = reader.IsDBNull(1) ? null : (int)reader.GetInt64(1),
                    Bandwidth = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    SizeBytes = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    Md5 = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        entry.Tracks = tracks;
        return entry;
    }

    /// <summary>
    /// 服务端列表查询：分页、搜索、合集过滤、跨源去重。
    /// <para>
    /// 与 <see cref="List"/> 的差别：<see cref="List"/> 只服务 CLI（取前 N 条就够），
    /// 这里要 <c>total</c>、要 offset、还要在聚合视图里按键去重。
    /// </para>
    /// <para>
    /// <b>去重只发生在聚合视图。</b>单源视图一行一条；聚合视图按 <c>key</c> 分组，
    /// 用窗口函数挑出"最该播的那条"（ok 优先、再按源 id），其余源只列名字。
    /// </para>
    /// </summary>
    public SummaryPage ListSummaries(SummaryQuery query)
    {
        var aggregate = query.SourceId is null;
        // sort=recommend：加权随机推荐序列。打分要在"去重后的候选全集"上做，
        // SQL 的 ORDER BY 帮不上忙（权重不在表里），所以走独立的内存打分路径。
        if (string.Equals(query.Sort, "recommend", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(query.Seed))
        {
            return ListSummariesRecommend(query, aggregate);
        }

        var partition = aggregate ? "PARTITION BY m.key" : "PARTITION BY m.source_id, m.key";
        var byAdded = string.Equals(query.Sort, "added", StringComparison.OrdinalIgnoreCase);

        // sort=recent：按"最后一次播"倒序 —— 这就是「继续观看」的口径。
        // 过滤条件（$recentOnly）也一起生效：没有 played_at 或位置还是 0 的条目
        // 属于"从没看过"，放进继续观看只会让列表里全是没开始的东西。
        var byRecent = string.Equals(query.Sort, "recent", StringComparison.OrdinalIgnoreCase);

        // 收藏夹的"加入时间"住在 favorite_item 里，而 Filter 只用了 EXISTS——EXISTS 能过滤但不能排序。
        // 排序参数生效时额外 LEFT JOIN 一次，把 added_at 拉进 CTE：
        // 外层 ORDER BY 看不到 CTE 里没 SELECT 出来的列，所以必须显式投影。
        // $fid 为 null 时这个 join 一行都匹配不上（NULL 比较恒假），退化成"全部 NULL"，不影响非收藏视图。
        var addedJoin = byAdded
            ? "LEFT JOIN favorite_item fi ON fi.favorite_id = $fid AND fi.source_id = m.source_id AND fi.key = m.key"
            : "";
        var addedColumn = byAdded ? ", fi.added_at AS added_at" : "";

        // played_at 在 media_meta 上（mt 已经 join 过了），不用额外 join；
        // 同样必须显式投影，否则外层 ORDER BY 看不到。
        var recentColumn = byRecent ? ", mt.last_play_at AS played_at" : "";

        var from =
            $"""
             FROM media m
             LEFT JOIN media_meta mt ON mt.source_id = m.source_id AND mt.key = m.key
             {addedJoin}
             WHERE {Filter(query)}
             """;

        // 默认按标题（稳定、与 Web 页一致）；sort=added 时最新加入的在前，
        // 时间相同（同一毫秒批量收藏）再按标题兜底，保证分页不会在不同请求间抖序。
        // sort=recent 同理：同一毫秒播过的（几乎不可能，但播放器重试会）按标题兜底。
        var orderBy = byAdded
            ? "f.added_at IS NULL, f.added_at DESC, f.title IS NULL, f.title, f.key"
            : byRecent
                ? "f.played_at IS NULL, f.played_at DESC, f.title IS NULL, f.title, f.key"
                : "f.title IS NULL, f.title, f.key";

        var baseCte =
            $"""
             WITH filtered AS (
                 SELECT m.source_id, m.key, m.layout,
                        COALESCE(mt.title_display, m.title_raw) AS title,
                        m.up_name, m.duration_ms, m.width, m.height, m.size_bytes, m.tag_code,
                        m.poster_rel, m.has_danmaku, COALESCE(mt.hidden, 0) AS hidden,
                        COALESCE(mt.play_pos_ms, 0) AS play_pos_ms, m.status,
                        COALESCE(mt.rotation, 0) AS rotation,
                        m.collection_id{addedColumn}{recentColumn},
                        ROW_NUMBER() OVER (
                            {partition}
                            ORDER BY CASE m.status WHEN 'ok' THEN 0 WHEN 'corrupt' THEN 1 ELSE 2 END,
                                     m.source_id
                        ) AS rn
                 {from}
             )
             """;

        int total;
        var rows = new List<MediaSummaryRow>();

        using (var count = db.Connection.CreateCommand())
        {
            count.CommandText = $"{baseCte} SELECT COUNT(*) FROM filtered WHERE rn = 1;";
            BindFilter(count, query);
            total = Convert.ToInt32(count.ExecuteScalar());
        }

        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText =
                $"""
                 {baseCte}
                 SELECT f.source_id, f.key, f.layout, f.title, f.up_name, f.duration_ms, f.width,
                        f.height, f.size_bytes, f.tag_code, f.poster_rel, f.has_danmaku, f.hidden,
                        f.play_pos_ms, f.status, COALESCE(f.collection_id, ''),
                        COALESCE(c.name, ''), COALESCE(c.kind, ''), f.rotation
                 FROM filtered f
                 LEFT JOIN collection c ON c.id = f.collection_id
                 WHERE f.rn = 1
                 ORDER BY {orderBy}
                 LIMIT $limit OFFSET $offset;
                 """;
            BindFilter(cmd, query);
            cmd.Parameters.AddWithValue("$limit", query.Limit);
            cmd.Parameters.AddWithValue("$offset", query.Offset);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MediaSummaryRow(
                    SourceId: reader.GetString(0),
                    Key: reader.GetString(1),
                    Layout: reader.GetString(2),
                    Title: reader.IsDBNull(3) ? null : reader.GetString(3),
                    UpName: reader.IsDBNull(4) ? null : reader.GetString(4),
                    DurationMs: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    Width: reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
                    Height: reader.IsDBNull(7) ? null : (int)reader.GetInt64(7),
                    SizeBytes: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    TagCode: reader.IsDBNull(9) ? null : (int)reader.GetInt64(9),
                    PosterRel: reader.IsDBNull(10) ? null : reader.GetString(10),
                    HasDanmaku: reader.GetInt64(11) != 0,
                    Hidden: reader.GetInt64(12) != 0,
                    PlayPosMs: reader.GetInt64(13),
                    Rotation: (int)reader.GetInt64(18),
                    Status: reader.GetString(14),
                    Sources: [],
                    Preferred: reader.GetString(0),
                    CollectionId: reader.GetString(15),
                    CollectionName: reader.GetString(16),
                    CollectionKind: reader.GetString(17)));
            }
        }

        // 只有聚合视图需要"这条内容还在哪些源里"。页大小封顶 500，所以是一次廉价的 IN 查询。
        if (aggregate && rows.Count > 0)
        {
            var map = SourcesFor(rows.Select(r => r.Key).ToList());
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i] = rows[i] with { Sources = map.TryGetValue(rows[i].Key, out var s) ? s : [rows[i].SourceId] };
            }
        }

        var nextOffset = query.Offset + rows.Count < total ? query.Offset + rows.Count : (int?)null;
        return new SummaryPage(total, rows, nextOffset);
    }

    /// <summary>推荐打分用的候选行。<see cref="Cnt"/> 是跨源合并到 key 维度的播放次数。</summary>
    private sealed record RecCandidate(string SourceId, string Key, int Cnt, long LastAt);

    /// <summary>
    /// 加权随机推荐序列（<c>sort=recommend</c>）。
    /// <para>
    /// 权重 = 1/(1+播放次数)，次数来自 <c>media_meta.play_count</c>（/meta 上报的跨设备
    /// 播放计数）：未看权重 1.0、播 1 次 0.5、播 n 次 1/(1+n)——一条公式同时满足"未看优先"
    /// 与"低播优先"，且是概率性的（高播也会偶现，不是绝迹）。
    /// </para>
    /// <para>
    /// 打分用加权洗牌（Efraimidis–Spirakis）：score = U^(1/w) = U^(1+cnt)，U 是由
    /// seed+key 哈希出的 (0,1) 均匀数。<b>确定性</b>：同一 seed 全序列固定，客户端翻页
    /// offset 递增就是走这条序列，服务端不存任何会话状态；换 seed 即换一批。
    /// 每 6 个槽位留 1 个"重温槽"，只从已看过的里面按同权重抽，高播内容有稳定的复出频率。
    /// </para>
    /// <para>
    /// 排除：30 分钟内播过的（刚看完别再推）；排除后剩得太少就不排除（小库会被清空）。
    /// </para>
    /// </summary>
    private SummaryPage ListSummariesRecommend(SummaryQuery query, bool aggregate)
    {
        var partition = aggregate ? "PARTITION BY m.key" : "PARTITION BY m.source_id, m.key";
        // 候选集与水合共用一份 CTE：过滤条件（含 missing/hidden/白名单）必须在 SQL 层生效，
        // 与普通分页路径完全同源，否则推荐序列会混进不该出现的条目。
        var cte =
            $"""
             WITH filtered AS (
                 SELECT m.source_id, m.key, m.layout,
                        COALESCE(mt.title_display, m.title_raw) AS title,
                        m.up_name, m.duration_ms, m.width, m.height, m.size_bytes, m.tag_code,
                        m.poster_rel, m.has_danmaku, COALESCE(mt.hidden, 0) AS hidden,
                        COALESCE(mt.play_pos_ms, 0) AS play_pos_ms, m.status,
                        COALESCE(mt.rotation, 0) AS rotation, m.collection_id,
                        ROW_NUMBER() OVER (
                            {partition}
                            ORDER BY CASE m.status WHEN 'ok' THEN 0 WHEN 'corrupt' THEN 1 ELSE 2 END,
                                     m.source_id
                        ) AS rn
                 FROM media m
                 LEFT JOIN media_meta mt ON mt.source_id = m.source_id AND mt.key = m.key
                 WHERE {Filter(query)}
             )
             """;

        // 1) 播放次数 + 最近播放时间。来源是 media_meta（/meta 上报的跨设备计数，
        //    MarkedPlayed 时 play_count+1），按 key 合并多源（次数求和、时间取最近）
        var counts = new Dictionary<string, (int Cnt, long LastAt)>(StringComparer.Ordinal);
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT key, COALESCE(SUM(play_count), 0), COALESCE(MAX(last_play_at), 0) FROM media_meta GROUP BY key;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                counts[reader.GetString(0)] = ((int)(long)reader.GetInt64(1), reader.GetInt64(2));
        }

        // 2) 候选全集（跨源去重后），只取键——内存打分用
        var candidates = new List<RecCandidate>();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = $"{cte} SELECT source_id, key FROM filtered WHERE rn = 1;";
            BindFilter(cmd, query);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(1);
                var (cnt, lastAt) = counts.TryGetValue(key, out var t) ? t : (0, 0L);
                candidates.Add(new RecCandidate(reader.GetString(0), key, cnt, lastAt));
            }
        }

        // 3) 排除刚播过的；排除后剩得太少就不排除（小库会被清空一页）
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RecentPlayMs;
        var kept = candidates.Where(c => c.LastAt <= cutoff).ToList();
        if (kept.Count >= MinRecommendPool) candidates = kept;

        // 4) 加权洗牌：score = U^(1+cnt)，降序。未看(cnt=0)就是纯 U 排序 = 均匀随机；
        //    播过的指数抬升使其自然沉底。重温池 = 主序列里的已看子集（同序即同权重序）。
        var score = new Dictionary<(string, string), double>(candidates.Count);
        foreach (var c in candidates)
            score[(c.SourceId, c.Key)] = Math.Pow(Uniform01(query.Seed!, c.Key), 1 + c.Cnt);

        var main = candidates
            .OrderByDescending(c => score[(c.SourceId, c.Key)])
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();
        var replay = main.Where(c => c.Cnt > 0).ToList();

        // 5) 槽位合并：每 6 个槽 1 个重温槽（只从已看里抽）；主槽跳过已被重温槽用掉的
        var used = new HashSet<(string, string)>();
        var ordered = new List<RecCandidate>(main.Count);
        int mi = 0, ri = 0;
        while (ordered.Count < main.Count)
        {
            var took = false;
            if (ordered.Count % RecommendReplayEvery == RecommendReplayEvery - 1)
            {
                while (ri < replay.Count && !used.Add((replay[ri].SourceId, replay[ri].Key))) ri++;
                if (ri < replay.Count)
                {
                    ordered.Add(replay[ri]);
                    ri++;
                    took = true;
                }
            }
            if (!took)
            {
                while (mi < main.Count && !used.Add((main[mi].SourceId, main[mi].Key))) mi++;
                if (mi >= main.Count) break; // 防御：replay ⊆ main，理论上到不了
                ordered.Add(main[mi]);
                mi++;
            }
        }

        // 6) 按推荐序切页，再按页内 key 集水合完整行（C# 端重排回推荐序）
        var total = ordered.Count;
        var pageKeys = ordered.Skip(query.Offset).Take(query.Limit).ToList();
        var rows = new List<MediaSummaryRow>(pageKeys.Count);
        if (pageKeys.Count > 0)
        {
            using var cmd = db.Connection.CreateCommand();
            var names = new List<string>(pageKeys.Count);
            for (var i = 0; i < pageKeys.Count; i++)
            {
                var name = $"$pk{i}";
                names.Add(name);
                cmd.Parameters.AddWithValue(name, pageKeys[i].Key);
            }

            cmd.CommandText =
                $"""
                 {cte}
                 SELECT f.source_id, f.key, f.layout, f.title, f.up_name, f.duration_ms, f.width,
                        f.height, f.size_bytes, f.tag_code, f.poster_rel, f.has_danmaku, f.hidden,
                        f.play_pos_ms, f.status, COALESCE(f.collection_id, ''),
                        COALESCE(c.name, ''), COALESCE(c.kind, ''), f.rotation
                 FROM filtered f
                 LEFT JOIN collection c ON c.id = f.collection_id
                 WHERE f.rn = 1 AND f.key IN ({string.Join(", ", names)});
                 """;
            BindFilter(cmd, query);

            var byKey = new Dictionary<string, MediaSummaryRow>(StringComparer.Ordinal);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var key = reader.GetString(1);
                    byKey[key] = new MediaSummaryRow(
                        SourceId: reader.GetString(0),
                        Key: key,
                        Layout: reader.GetString(2),
                        Title: reader.IsDBNull(3) ? null : reader.GetString(3),
                        UpName: reader.IsDBNull(4) ? null : reader.GetString(4),
                        DurationMs: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        Width: reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
                        Height: reader.IsDBNull(7) ? null : (int)reader.GetInt64(7),
                        SizeBytes: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        TagCode: reader.IsDBNull(9) ? null : (int)reader.GetInt64(9),
                        PosterRel: reader.IsDBNull(10) ? null : reader.GetString(10),
                        HasDanmaku: reader.GetInt64(11) != 0,
                        Hidden: reader.GetInt64(12) != 0,
                        PlayPosMs: reader.GetInt64(13),
                        Rotation: (int)reader.GetInt64(18),
                        Status: reader.GetString(14),
                        Sources: [],
                        Preferred: reader.GetString(0),
                        CollectionId: reader.GetString(15),
                        CollectionName: reader.GetString(16),
                        CollectionKind: reader.GetString(17));
                }
            }

            foreach (var c in pageKeys)
                if (byKey.TryGetValue(c.Key, out var row))
                    rows.Add(row);
        }

        // 聚合视图补"这条内容还在哪些源里"（与普通路径同一待遇，页大小 ≤500，一次廉价 IN）
        if (aggregate && rows.Count > 0)
        {
            var map = SourcesFor(rows.Select(r => r.Key).ToList());
            for (var i = 0; i < rows.Count; i++)
                rows[i] = rows[i] with { Sources = map.TryGetValue(rows[i].Key, out var s) ? s : [rows[i].SourceId] };
        }

        var nextOffset = query.Offset + rows.Count < total ? query.Offset + rows.Count : (int?)null;
        return new SummaryPage(total, rows, nextOffset);
    }

    /// <summary>推荐序列的常量：刚播过的冷却窗口 / 小库保护阈值 / 重温槽周期。</summary>
    private const long RecentPlayMs = 30 * 60 * 1000;
    private const int MinRecommendPool = 10;
    private const int RecommendReplayEvery = 6;

    /// <summary>seed+key 的确定性均匀数 (0,1)。SHA256 前 8 字节取高 53 位，双精度下无精度损失。</summary>
    private static double Uniform01(string seed, string key)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed + "|" + key));
        var v = BitConverter.ToUInt64(bytes, 0);
        return (v >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>一批 key 各自出现在哪些源里。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SourcesFor(IReadOnlyList<string> keys)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (keys.Count == 0) return new Dictionary<string, IReadOnlyList<string>>();

        using var cmd = db.Connection.CreateCommand();
        var names = new List<string>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            var name = $"$k{i}";
            names.Add(name);
            cmd.Parameters.AddWithValue(name, keys[i]);
        }

        cmd.CommandText =
            $"SELECT key, source_id FROM media WHERE key IN ({string.Join(", ", names)}) ORDER BY key, source_id;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(reader.GetString(1));
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>WHERE 子句。拼进两个查询，所以必须抽出来共用一份，否则 total 与 items 会不一致。</summary>
    private static string Filter(SummaryQuery query) =>
        """
        ($sid IS NULL OR m.source_id = $sid)
          AND ($scope IS NULL
               OR m.source_id IN (SELECT value FROM json_each($scope)))
          AND ($includeHidden = 1 OR COALESCE(mt.hidden, 0) = 0)
          -- status='missing' = 扫描时文件已不在盘上。列表默认不发出去：手机端没有"文件被删"
          -- 的概念，收到也只是播不出来；Web 管理页传 includeMissing=1 才看全量（打"缺失"标）。
          AND ($includeMissing = 1 OR m.status <> 'missing')
          AND ($like IS NULL
               OR COALESCE(mt.title_display, m.title_raw) LIKE $like ESCAPE '\'
               OR m.up_name LIKE $like ESCAPE '\'
               OR m.rel_path LIKE $like ESCAPE '\')
          AND ($cid IS NULL OR m.collection_id = $cid)
          -- sort=recent（继续观看）：只留"真的看过一点"的条目。只判 played_at 不够——
          -- 播了 1 秒就关也会写 played_at，那属于"点开过"，不是"看到一半"。
          AND ($recentOnly = 0
               OR (mt.last_play_at IS NOT NULL AND COALESCE(mt.play_pos_ms, 0) > 0))
          AND ($fid IS NULL
               OR EXISTS (SELECT 1 FROM favorite_item fi
                          WHERE fi.favorite_id = $fid
                            AND fi.source_id = m.source_id AND fi.key = m.key))
        """;

    private static void BindFilter(SqliteCommand cmd, SummaryQuery query)
    {
        cmd.Parameters.AddWithValue("$sid", (object?)query.SourceId ?? DBNull.Value);

        // 源白名单用 json_each 展开，而不是拼一段 IN (?,?,?)：
        // 后者要为每个源生成占位符，SQL 文本随源数量变化，SQLite 也要为每种文本准备一次查询计划。
        // 传 null 表示"不限制"，与 $sid 的分工是——$sid 选单个源（单源视图），这里限可见集合（服务视角）。
        cmd.Parameters.AddWithValue("$scope", (object?)ScopeJson(query.SourceIds) ?? DBNull.Value);

        cmd.Parameters.AddWithValue("$includeHidden", query.IncludeHidden ? 1 : 0);
        cmd.Parameters.AddWithValue("$includeMissing", query.IncludeMissing ? 1 : 0);
        cmd.Parameters.AddWithValue("$like", (object?)LikePattern(query.Search) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cid", (object?)query.CollectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$recentOnly",
            string.Equals(query.Sort, "recent", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        cmd.Parameters.AddWithValue("$fid", (object?)query.FavoriteId ?? DBNull.Value);
    }

    private static string? ScopeJson(IReadOnlyCollection<string>? sourceIds) =>
        sourceIds is null ? null : JsonSerializer.Serialize(sourceIds);

    private static string? LikePattern(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;

        var escaped = search.Trim()
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

        return $"%{escaped}%";
    }

    private static SourceLayout LayoutFromDb(string value) =>
        value == "dual" ? SourceLayout.Dual : SourceLayout.Single;

    private static MediaStatus StatusFromDb(string value) => value switch
    {
        "ok" => MediaStatus.Ok,
        "corrupt" => MediaStatus.Corrupt,
        _ => MediaStatus.Missing,
    };

    private static string StatusToDb(MediaStatus status) => status switch
    {
        MediaStatus.Ok => "ok",
        MediaStatus.Corrupt => "corrupt",
        _ => "missing",
    };
}

/// <summary>列表视图的一行。<c>Status</c> 保留字符串形态，方便 CLI 与接口原样输出。</summary>
public sealed record MediaListRow(
    string SourceId,
    string Key,
    string? Title,
    string RelPath,
    string? RelDir,
    int? Width,
    int? Height,
    long? DurationMs,
    string? PosterRel,
    string Status,
    string? Codec,
    bool Hidden = false,
    bool TitleOverridden = false);

/// <summary>
/// 服务端列表查询的参数。
/// <para><see cref="SourceId"/> 为 null 即"全部视频"聚合视图——去重只在这个视图里发生。</para>
/// <para>
/// <see cref="SourceIds"/> 是<b>可见源白名单</b>，由"当前请求落在哪个对外服务上"决定（见 Server 的
/// <c>ServiceScope</c>）。为 null 表示不限制。它与 <see cref="SourceId"/> 的分工是：
/// 前者回答"这个服务能看到哪些源"，后者回答"这次要看哪个源"。
/// </para>
/// <para>
/// <b>必须在 SQL 层过滤，不能查完再筛。</b>否则 <c>total</c> 与分页游标都按全库算，
/// 客户端会看到"总数 5427、翻到第 3 页就空了"。
/// </para>
/// <para>
/// <see cref="Sort"/> 目前只有 <c>title</c>（默认，null 同义）与 <c>added</c>
/// （按收藏夹的加入时间倒序，只在带 <see cref="FavoriteId"/> 时有意义）。
/// 之所以做成参数而不是改默认：Web 页与单源视图靠标题序找片子，改了默认就是另一套体验；
/// 而收藏夹的"连续播放"要的是加入顺序，两者诉求相反。
/// </para>
/// </summary>
public sealed record SummaryQuery(
    string? SourceId = null,
    string? Search = null,
    string? CollectionId = null,
    bool IncludeHidden = false,
    bool IncludeMissing = false,
    int Offset = 0,
    int Limit = 100,
    IReadOnlyCollection<string>? SourceIds = null,
    string? FavoriteId = null,
    string? Sort = null,
    /// <summary>
    /// <c>sort=recommend</c> 的确定性随机种子（客户端每次进信息流生成一次，会话内复用）。
    /// 同一 seed + 同一数据 = 同一序列，翻页才不会抖；换 seed = 换一批推荐。
    /// </summary>
    string? Seed = null);

/// <summary>一页结果。<see cref="NextOffset"/> 为 null 表示到底了。</summary>
public sealed record SummaryPage(int Total, IReadOnlyList<MediaSummaryRow> Items, int? NextOffset);

/// <summary>
/// 列表的一行，字段是 <c>GET /library</c> 需要的最小集。
/// <para>
/// <see cref="Sources"/> 只在聚合视图里非空；<see cref="Preferred"/> 是"该播哪条"的答案。
/// </para>
/// </summary>
public sealed record MediaSummaryRow(
    string SourceId,
    string Key,
    string Layout,
    string? Title,
    string? UpName,
    long? DurationMs,
    int? Width,
    int? Height,
    long? SizeBytes,
    int? TagCode,
    string? PosterRel,
    bool HasDanmaku,
    bool Hidden,
    long PlayPosMs,
    /// <summary>用户覆盖的旋转角（media_meta.rotation，0/90/180/270）。</summary>
    int Rotation,
    string Status,
    IReadOnlyList<string> Sources,
    string Preferred,
    string CollectionId,
    string CollectionName,
    string CollectionKind);
