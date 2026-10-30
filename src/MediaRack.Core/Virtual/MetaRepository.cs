using System.Text.Json;
using MediaRack.Models;
using MediaRack.Store;
using Microsoft.Data.Sqlite;

namespace MediaRack.Virtual;

/// <summary>
/// 虚拟元数据的读写。这是<b>整个系统里唯一会"改变内容呈现"的写入口</b>。
/// <para>
/// 三条硬规则，全部在这里落地：
/// </para>
/// <list type="number">
/// <item>只写 <c>media_meta</c> 一张表，不产生任何指向源目录的写操作；</item>
/// <item>写之前先过 <see cref="VirtualWrite"/> 闸门，被拒绝时不留下半条记录；</item>
/// <item>每次成功写入 <c>rev</c> 单调 +1；带 <c>expectedRev</c> 时不一致返回冲突。</item>
/// </list>
/// </summary>
public sealed class MetaRepository(LibraryDatabase db)
{
    private const string SelectSql =
        """
        SELECT title_display, tags, hidden, rotation, play_pos_ms, play_count,
               last_play_at, rev, updated_at, updated_by, collection
        FROM media_meta
        WHERE source_id = $sid AND key = $key;
        """;

    private const string UpsertSql =
        """
        INSERT INTO media_meta (
            source_id, key, title_display, tags, hidden, rotation,
            play_pos_ms, play_count, last_play_at, rev, updated_at, updated_by, collection)
        VALUES (
            $sid, $key, $title, $tags, $hidden, $rotation,
            $pos, $count, $lastPlay, $rev, $updated, $by, $collection)
        ON CONFLICT(source_id, key) DO UPDATE SET
            title_display = excluded.title_display,
            tags          = excluded.tags,
            hidden        = excluded.hidden,
            rotation      = excluded.rotation,
            play_pos_ms   = excluded.play_pos_ms,
            play_count    = excluded.play_count,
            last_play_at  = excluded.last_play_at,
            rev           = excluded.rev,
            updated_at    = excluded.updated_at,
            updated_by    = excluded.updated_by,
            collection    = excluded.collection;
        """;

    /// <summary>读一条的虚拟元数据；没有记录时返回 null（语义等于"全部跟随源端"）。</summary>
    public MediaMeta? Get(string sourceId, string key)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = SelectSql;
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$key", key);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(sourceId, key, reader) : null;
    }

    /// <summary>合并源端值与虚拟值。条目不存在返回 null。</summary>
    public ResolvedMedia? Resolve(string sourceId, string key)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT title_raw FROM media WHERE source_id = $sid AND key = $key;";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$key", key);

        string? titleRaw;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
            titleRaw = reader.IsDBNull(0) ? null : reader.GetString(0);
        }

        return new ResolvedMedia(sourceId, key, titleRaw, Get(sourceId, key) ?? Empty(sourceId, key));
    }

    /// <summary>
    /// 应用一个补丁。
    /// </summary>
    /// <param name="expectedRev">给了就做乐观锁：与服务端当前 <c>rev</c> 不一致返回冲突。默认不做。</param>
    /// <param name="actor">写入方标识，便于排障；不参与冲突判定。</param>
    public WriteResult Apply(
        string sourceId, string key, MetaPatch patch,
        long? expectedRev = null, string? actor = null)
    {
        var source = new SourceRepository(db).Get(sourceId);
        if (source is null) return WriteResult.NotFound($"源未登记：{sourceId}");

        if (!MediaExists(sourceId, key)) return WriteResult.NotFound($"条目不存在：{sourceId}/{key}");

        // 闸门放在写入之前：被拒绝时库里连一条空记录都不会留下。
        foreach (var field in MetaPolicy.FieldsOf(patch))
        {
            if (!MetaPolicy.Allows(source.VirtualWrite, field))
            {
                return WriteResult.Forbidden(
                    $"源 {sourceId} 的虚拟写权限为 {MetaPolicy.Name(source.VirtualWrite)}，" +
                    $"不允许修改{MetaPolicy.Name(field)}");
            }
        }

        var current = Get(sourceId, key) ?? Empty(sourceId, key);

        if (patch.IsEmpty) return WriteResult.Applied(current);

        if (expectedRev is { } expected && expected != current.Rev)
        {
            return WriteResult.Conflict(
                $"rev 不符：If-Match {expected}，服务端当前 {current.Rev}", current);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var next = Merge(current, patch) with
        {
            Rev = current.Rev + 1,
            UpdatedAt = now,
            UpdatedBy = actor,
        };
        using var tx = db.Connection.BeginTransaction();
        Write(next, tx);
        tx.Commit();

        return WriteResult.Applied(next);
    }

    /// <summary>
    /// 把一条 meta 原样复制到另一个条目名下（内容指纹迁移用）。
    /// 目标已有 meta 时不覆盖——目标位置上用户可能已经改过设置，认领只填空位。
    /// 复制是系统行为，不走 <see cref="Apply"/> 的策略闸门与 rev 冲突判定。
    /// </summary>
    /// <returns>是否真的复制了（源无 meta 或目标已有 meta 都算没复制）。</returns>
    public bool CopyMeta(string fromSource, string fromKey, string toSource, string toKey, string? actor)
    {
        if (fromSource == toSource && fromKey == toKey) return false;
        if (Get(fromSource, fromKey) is null) return false;
        if (Get(toSource, toKey) is not null) return false;

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO media_meta (
                source_id, key, title_display, tags, hidden, rotation,
                play_pos_ms, play_count, last_play_at, rev, updated_at, updated_by, collection)
            SELECT $toSid, $toKey, title_display, tags, hidden, rotation,
                   play_pos_ms, play_count, last_play_at, rev, $now, $by, collection
            FROM media_meta
            WHERE source_id = $fromSid AND key = $fromKey;
            """;
        cmd.Parameters.AddWithValue("$toSid", toSource);
        cmd.Parameters.AddWithValue("$toKey", toKey);
        cmd.Parameters.AddWithValue("$fromSid", fromSource);
        cmd.Parameters.AddWithValue("$fromKey", fromKey);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$by", (object?)actor ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    private bool MediaExists(string sourceId, string key)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM media WHERE source_id = $sid AND key = $key;";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is not null;
    }

    private static MediaMeta Empty(string sourceId, string key) =>
        new() { SourceId = sourceId, Key = key };

    private static MediaMeta Merge(MediaMeta current, MetaPatch patch) => current with
    {
        TitleDisplay = patch.TitleDisplay is null
            ? current.TitleDisplay
            : NormalizeTitle(patch.TitleDisplay),
        Tags = patch.ClearTags
            ? []
            : patch.Tags is not null
                ? NormalizeTags(patch.Tags)
                : NormalizeTags(ApplyTagDelta(current.Tags, patch.TagsAdd, patch.TagsRemove)),
        Hidden = patch.Hidden ?? current.Hidden,
        Rotation = patch.Rotation is { } rotation ? NormalizeRotation(rotation) : current.Rotation,
        PlayPosMs = patch.PlayPosMs is { } pos ? Math.Max(0, pos) : current.PlayPosMs,
        PlayCount = patch.MarkedPlayed ? current.PlayCount + 1 : current.PlayCount,
        LastPlayAt = patch.MarkedPlayed
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : current.LastPlayAt,
        Collection = patch.Collection is null
            ? current.Collection
            : NormalizeCollection(patch.Collection),
    };

    /// <summary>
    /// 合集 override 键校验：空串/全空白 = 清除（恢复投影）；非空必须是
    /// <c>dir:</c> / <c>up:</c> / <c>custom:</c> 三种去源化前缀之一。
    /// </summary>
    private static string? NormalizeCollection(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;

        if (!trimmed.StartsWith("dir:") && !trimmed.StartsWith("up:") && !trimmed.StartsWith("custom:"))
        {
            throw new ArgumentException(
                $"collection 键必须是 dir:/… 、up:… 或 custom:… 之一，收到 {trimmed}");
        }

        return trimmed;
    }

    /// <summary>空串 / 全空白视为"清除覆盖"——恢复源端标题不需要单独一个命令。</summary>
    private static string? NormalizeTitle(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>去空白、去重（忽略大小写，保留首次出现的写法与顺序）。</summary>
    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in tags)
        {
            var tag = raw.Trim();
            if (tag.Length == 0 || !seen.Add(tag)) continue;
            result.Add(tag);
        }

        return result;
    }

    private static IEnumerable<string> ApplyTagDelta(
        IReadOnlyList<string> current, IReadOnlyList<string>? add, IReadOnlyList<string>? remove)
    {
        var set = new List<string>(current);
        if (remove is not null)
        {
            var drop = new HashSet<string>(remove.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
            set.RemoveAll(t => drop.Contains(t));
        }

        if (add is not null) set.AddRange(add);
        return set;
    }

    private static int NormalizeRotation(int rotation)
    {
        var normalized = ((rotation % 360) + 360) % 360;
        if (normalized % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "旋转角必须是 90 的整数倍");
        }

        return normalized;
    }

    private static MediaMeta ReadRow(string sourceId, string key, SqliteDataReader reader) => new()
    {
        SourceId = sourceId,
        Key = key,
        TitleDisplay = reader.IsDBNull(0) ? null : reader.GetString(0),
        Tags = reader.IsDBNull(1) ? [] : ParseTags(reader.GetString(1)),
        Hidden = reader.GetInt64(2) != 0,
        Rotation = (int)reader.GetInt64(3),
        PlayPosMs = reader.GetInt64(4),
        PlayCount = (int)reader.GetInt64(5),
        LastPlayAt = reader.IsDBNull(6) ? null : reader.GetInt64(6),
        Rev = reader.GetInt64(7),
        UpdatedAt = reader.IsDBNull(8) ? null : reader.GetInt64(8),
        UpdatedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
        Collection = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static IReadOnlyList<string> ParseTags(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            // 库里出现坏 JSON 时宁可丢标签也不能让整条读不出来。
            return [];
        }
    }

    private void Write(MediaMeta meta, SqliteTransaction tx)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = UpsertSql;
        cmd.Parameters.AddWithValue("$sid", meta.SourceId);
        cmd.Parameters.AddWithValue("$key", meta.Key);
        cmd.Parameters.AddWithValue("$title", (object?)meta.TitleDisplay ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags",
            meta.Tags.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(meta.Tags));
        cmd.Parameters.AddWithValue("$hidden", meta.Hidden ? 1 : 0);
        cmd.Parameters.AddWithValue("$rotation", meta.Rotation);
        cmd.Parameters.AddWithValue("$pos", meta.PlayPosMs);
        cmd.Parameters.AddWithValue("$count", meta.PlayCount);
        cmd.Parameters.AddWithValue("$lastPlay", (object?)meta.LastPlayAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rev", meta.Rev);
        cmd.Parameters.AddWithValue("$updated", (object?)meta.UpdatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$by", (object?)meta.UpdatedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$collection", (object?)meta.Collection ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
