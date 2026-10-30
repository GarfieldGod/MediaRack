using MediaRack.Models;
using Microsoft.Data.Sqlite;

namespace MediaRack.Store;

/// <summary>源的登记与读取。源本身只读，这里只维护"它在哪里、怎么解释它、谁能进来"。</summary>
public sealed class SourceRepository(LibraryDatabase db)
{
    private const string Columns =
        "id, name, layout, root, virtual_write, enabled, scan_include, last_scan_at, last_count, slug, token";

    public void Upsert(SourceConfig source, int? itemCount = null, long? lastScanAt = null)
    {
        // 身份两列：显式给了就用，否则沿用库里已有的，都没有才生成。
        // 放在这里而不是只靠启动时补齐，是因为"新源"不止管理页一条路——
        // 扫描作业带 root 进来同样会 Upsert，那条路径不生成身份，
        // 结果就是新源永远没有 /s/{slug}，谁也访问不到。
        var existing = Get(source.Id);
        var slug = source.Slug ?? existing?.Slug
            ?? UniqueSlug(SourceIdentity.CandidateSlug(source.Id, source.Name), source.Id);
        var token = source.Token ?? existing?.Token ?? SourceIdentity.NewToken();

        // root/layout 是 v3 之前的遗留列（NOT NULL 约束还在）：写首个文件夹作占位，
        // 真相在 source_folder 表。零文件夹的源写空串/single。
        var root = source.Root
            ?? source.Folders.FirstOrDefault()?.Root
            ?? existing?.Root
            ?? string.Empty;

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            $"""
            INSERT INTO source ({Columns})
            VALUES ($id, $name, $layout, $root, $vw, $enabled, $include, $scanAt, $count, $slug, $token)
            ON CONFLICT(id) DO UPDATE SET
                name          = excluded.name,
                layout        = excluded.layout,
                root          = excluded.root,
                virtual_write = excluded.virtual_write,
                enabled       = excluded.enabled,
                scan_include  = excluded.scan_include,
                last_scan_at  = COALESCE(excluded.last_scan_at, source.last_scan_at),
                last_count    = COALESCE(excluded.last_count, source.last_count),
                -- 身份两列必须 COALESCE 兜住：扫描作业构造 SourceConfig 时不带 slug/token，
                -- 直接 excluded.slug 会把已经分配的身份清成 NULL，客户端当场失联。
                slug          = COALESCE(excluded.slug, source.slug),
                token         = COALESCE(excluded.token, source.token);
            """;

        cmd.Parameters.AddWithValue("$id", source.Id);
        cmd.Parameters.AddWithValue("$name", source.Name);
        cmd.Parameters.AddWithValue("$layout", LayoutToDb(source.Layout));
        cmd.Parameters.AddWithValue("$root", root);
        cmd.Parameters.AddWithValue("$vw", VirtualWriteToDb(source.VirtualWrite));
        cmd.Parameters.AddWithValue("$enabled", source.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$include", (object?)source.ScanInclude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scanAt", (object?)lastScanAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$count", (object?)itemCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.Parameters.AddWithValue("$token", token);

        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SourceConfig> List()
    {
        var list = new List<SourceConfig>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM source ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadRow(reader));
        AttachFolders(list);
        return list;
    }

    // ── 文件夹列表（v3：一源多文件夹，布局跟文件夹走）──────────────

    /// <summary>某源的全部文件夹，按展示顺序。</summary>
    public IReadOnlyList<SourceFolder> ListFolders(string sourceId)
    {
        var folders = new List<SourceFolder>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, position, root, layout FROM source_folder WHERE source_id = $id ORDER BY position, id;";
        cmd.Parameters.AddWithValue("$id", sourceId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) folders.Add(ReadFolder(reader));
        return folders;
    }

    /// <summary>登记一个文件夹。同一源内路径唯一，重复返回 null（调用方报 409）。</summary>
    public SourceFolder? AddFolder(string sourceId, string root, SourceLayout? layout)
    {
        if (ListFolders(sourceId).Any(f => string.Equals(f.Root, root, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var existing = ListFolders(sourceId);
        var position = existing.Count == 0 ? 0 : existing.Max(f => f.Position) + 1;

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO source_folder (source_id, position, root, layout)
            VALUES ($sid, $pos, $root, $layout);
            """;
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$pos", position);
        cmd.Parameters.AddWithValue("$root", root);
        cmd.Parameters.AddWithValue("$layout", FolderLayoutToDb(layout));
        cmd.ExecuteNonQuery();

        return GetFolder(sourceId, LastInsertId());
    }

    public SourceFolder? GetFolder(string sourceId, long folderId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, position, root, layout FROM source_folder WHERE source_id = $sid AND id = $fid;";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$fid", folderId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFolder(reader) : null;
    }

    /// <summary>改文件夹布局。auto 由扫描时写回具体值，这里接受任意值（null = auto）。</summary>
    public bool SetFolderLayout(string sourceId, long folderId, SourceLayout? layout)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            "UPDATE source_folder SET layout = $layout WHERE source_id = $sid AND id = $fid;";
        cmd.Parameters.AddWithValue("$layout", FolderLayoutToDb(layout));
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$fid", folderId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>移除文件夹。条目不在这删——随后的重扫会把它下面的条目标 missing（磁盘文件不动）。</summary>
    public bool RemoveFolder(string sourceId, long folderId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM source_folder WHERE source_id = $sid AND id = $fid;";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$fid", folderId);
        return cmd.ExecuteNonQuery() > 0;
    }

    private long LastInsertId()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid();";
        return (long)cmd.ExecuteScalar()!;
    }

    private static SourceFolder ReadFolder(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Position: (int)reader.GetInt64(1),
        Root: reader.GetString(2),
        Layout: FolderLayoutFromDb(reader.GetString(3)));

    private static string FolderLayoutToDb(SourceLayout? layout) => layout switch
    {
        null => "auto",
        SourceLayout.Dual => "dual",
        _ => "single",
    };

    private static SourceLayout? FolderLayoutFromDb(string value) => value switch
    {
        "dual" => SourceLayout.Dual,
        "single" => SourceLayout.Single,
        _ => null, // auto
    };

    /// <summary>把 source_folder 的行按 source 分组拼回各 SourceConfig（一次查询，不逐源往返）。</summary>
    private void AttachFolders(List<SourceConfig> list)
    {
        if (list.Count == 0) return;

        var bySource = new Dictionary<string, List<SourceFolder>>(StringComparer.Ordinal);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT source_id, id, position, root, layout FROM source_folder ORDER BY position, id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sid = reader.GetString(0);
            if (!bySource.TryGetValue(sid, out var bucket))
            {
                bucket = [];
                bySource[sid] = bucket;
            }

            bucket.Add(new SourceFolder(
                Id: reader.GetInt64(1),
                Position: (int)reader.GetInt64(2),
                Root: reader.GetString(3),
                Layout: FolderLayoutFromDb(reader.GetString(4))));
        }

        foreach (var source in list)
        {
            if (bySource.TryGetValue(source.Id, out var folders))
            {
                source.Folders = folders;
            }
        }
    }

    public SourceConfig? Get(string id) =>
        List().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>按 URL 段查源。源门户每个请求都要走一次，所以直接 SQL 查而不是全表扫。</summary>
    public SourceConfig? GetBySlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM source WHERE slug = $slug;";
        cmd.Parameters.AddWithValue("$slug", slug.Trim());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    /// <summary>
    /// 改某个源的虚拟写权限。这是<b>源级管理操作</b>，不是条目写入，
    /// 所以它本身不受 <c>virtualWrite</c> 约束——否则 <c>none</c> 一旦设错就再也解不开。
    /// </summary>
    public bool SetVirtualWrite(string id, VirtualWrite level)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE source SET virtual_write = $vw WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$vw", VirtualWriteToDb(level));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetName(string id, string name)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE source SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>改 URL 段。调用方负责先查重（<see cref="IsSlugTaken"/>）。</summary>
    public bool SetSlug(string id, string slug)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE source SET slug = $slug WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$slug", slug);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetToken(string id, string token)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE source SET token = $token WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$token", token);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>slug 是否已被别的源占用（<paramref name="exceptId"/> 是"自己"，改名时排除）。</summary>
    public bool IsSlugTaken(string slug, string? exceptId = null) =>
        List().Any(s =>
            string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(s.Id, exceptId, StringComparison.Ordinal));

    /// <summary>分配一个未被占用的 slug：候选撞了就加 <c>-2</c>、<c>-3</c>……</summary>
    public string UniqueSlug(string candidate, string? exceptId = null)
    {
        var baseSlug = SourceIdentity.IsValidSlug(candidate)
            ? candidate
            : SourceIdentity.FallbackSlug(candidate);

        if (!IsSlugTaken(baseSlug, exceptId)) return baseSlug;

        for (var n = 2; n < 1000; n++)
        {
            // 后缀会撑长 slug，超长就从尾部截断再来。
            var suffix = "-" + n;
            var trimmed = baseSlug.Length + suffix.Length > SourceIdentity.MaxSlugLength
                ? baseSlug[..(SourceIdentity.MaxSlugLength - suffix.Length)].TrimEnd('-')
                : baseSlug;
            var attempt = trimmed + suffix;
            if (!IsSlugTaken(attempt, exceptId)) return attempt;
        }

        return SourceIdentity.FallbackSlug(baseSlug + Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 给还没有身份的源补齐 slug 与 token。<b>幂等</b>，可每次启动都调。
    /// <para>
    /// 放在这里而不是 SQL 迁移里，是因为规则本身是代码（slug 化、哈希回退、令牌随机），
    /// 用纯 SQL 表达不但要做一堆 <c>replace()</c>，还没法生成随机令牌。
    /// </para>
    /// </summary>
    public int EnsureIdentity()
    {
        var all = List();
        var pending = all.Where(s =>
            string.IsNullOrWhiteSpace(s.Slug) || string.IsNullOrWhiteSpace(s.Token)).ToList();

        if (pending.Count == 0) return 0;

        foreach (var source in pending)
        {
            var slug = string.IsNullOrWhiteSpace(source.Slug)
                ? UniqueSlug(SourceIdentity.CandidateSlug(source.Id, source.Name), source.Id)
                : source.Slug;

            var token = string.IsNullOrWhiteSpace(source.Token)
                ? SourceIdentity.NewToken()
                : source.Token;

            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "UPDATE source SET slug = $slug, token = $token WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", source.Id);
            cmd.Parameters.AddWithValue("$slug", slug);
            cmd.Parameters.AddWithValue("$token", token);
            cmd.ExecuteNonQuery();
        }

        return pending.Count;
    }

    private static SourceConfig ReadRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Layout = reader.GetString(2) == "dual" ? SourceLayout.Dual : SourceLayout.Single,
        Root = reader.GetString(3),
        VirtualWrite = reader.GetString(4) switch
        {
            "none" => VirtualWrite.None,
            "whitelist" => VirtualWrite.Whitelist,
            _ => VirtualWrite.Full,
        },
        Enabled = reader.GetInt64(5) != 0,
        ScanInclude = reader.IsDBNull(6) ? null : reader.GetString(6),
        LastScanAt = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        LastCount = reader.IsDBNull(8) ? null : (int)reader.GetInt64(8),
        Slug = reader.IsDBNull(9) ? null : reader.GetString(9),
        Token = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static string LayoutToDb(SourceLayout layout) =>
        layout == SourceLayout.Dual ? "dual" : "single";

    private static string VirtualWriteToDb(VirtualWrite vw) => vw switch
    {
        VirtualWrite.None => "none",
        VirtualWrite.Whitelist => "whitelist",
        _ => "full",
    };
}
