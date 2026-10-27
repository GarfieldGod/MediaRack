using Microsoft.Data.Sqlite;

namespace MediaRack.Store;

/// <summary>
/// 元数据库。<b>唯一可写的东西</b>——所有虚拟管理都落在这里，源目录永远只读。
/// <para>
/// 库放在 <c>&lt;meta_root&gt;/library.db</c>，可整体删除后由扫描重建。
/// </para>
/// </summary>
public sealed class LibraryDatabase : IDisposable
{
    public const string FileName = "library.db";

    /// <summary>程序支持的最高 schema 版本。</summary>
    public static int SupportedVersion => Schema.CurrentVersion;

    private readonly SqliteConnection _connection;

    private LibraryDatabase(SqliteConnection connection, string filePath)
    {
        _connection = connection;
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>供仓储层使用的连接。仅程序集内部可见。</summary>
    internal SqliteConnection Connection => _connection;

    /// <summary>当前库的 schema 版本；0 表示空库。</summary>
    public int Version => ReadVersion();

    /// <summary>库文件路径。参数可以是 meta 目录，也可以是 <c>.db</c> 文件路径。</summary>
    public static string ResolvePath(string pathOrMetaDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrMetaDir);
        return pathOrMetaDir.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(pathOrMetaDir)
            : Path.Combine(Path.GetFullPath(pathOrMetaDir), FileName);
    }

    /// <summary>
    /// 打开库。
    /// </summary>
    /// <param name="create">
    /// 为 true 时库不存在就建（并允许写入）；为 false 时库必须已存在。
    /// <b>注意</b>：<c>create: false</c> 不等于只读——它只表示"不许顺手建库"。
    /// </param>
    /// <param name="readOnly">严格只读连接。用于体检类命令，任何写入都会立刻报错。</param>
    public static LibraryDatabase Open(string pathOrMetaDir, bool create = true, bool readOnly = false)
    {
        var filePath = ResolvePath(pathOrMetaDir);

        if (!create && !File.Exists(filePath))
        {
            throw new FileNotFoundException($"库不存在：{filePath}", filePath);
        }

        if (!readOnly)
        {
            var parent = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        }

        var mode = readOnly
            ? SqliteOpenMode.ReadOnly
            : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = mode,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        // 服务端是多请求并发：WAL 允许"多读一写"，但写撞写仍会 SQLITE_BUSY。
        // 给 5 秒重试窗口，比让写入直接失败合理——个人库的写事务都是毫秒级。
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }

        return new LibraryDatabase(connection, filePath);
    }

    /// <summary>把库升级到 <see cref="Schema.CurrentVersion"/>。幂等，可反复调用。</summary>
    public MigrationResult Migrate()
    {
        // WAL 必须在事务外设置，且对内存库无效。
        Exec("PRAGMA journal_mode = WAL;");
        Exec("PRAGMA foreign_keys = ON;");

        var from = ReadVersion();
        if (from > Schema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"库的 schema 版本 {from} 高于程序支持的 {Schema.CurrentVersion}，请升级 MediaRack。");
        }

        var applied = new List<int>();
        for (var v = from + 1; v <= Schema.CurrentVersion; v++)
        {
            Exec(Schema.Load(v));
            UpsertVersion(v);
            applied.Add(v);
        }

        // 迁移之后补齐源身份（slug / token）。每次都跑：幂等，且新建的源可能是
        // 扫描作业 Upsert 进来的——那条路径不生成身份，只保留库里已有的值。
        if (TableNames().Contains("source", StringComparer.Ordinal))
        {
            new SourceRepository(this).EnsureIdentity();
        }

        // 合集是 media 的投影，重新投影一次很便宜，而且保证任何打开库的路径
        // （服务端 / CLI / 测试）拿到的合集都跟条目一致——包括刚迁移上来的老库。
        if (TableNames().Contains("media", StringComparer.Ordinal))
        {
            new CollectionRebuilder(this).Rebuild();
        }

        return new MigrationResult(from, Schema.CurrentVersion, applied, from != Schema.CurrentVersion);
    }

    /// <summary>库内已有的表名，按名称排序。</summary>
    public IReadOnlyList<string> TableNames() =>
        QueryStrings("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");

    public long CountRows(string table)
    {
        // 表名来自内部调用，不接受外部拼接；仍做一次白名单校验防误用。
        if (TableNames().Contains(table, StringComparer.Ordinal) is false)
        {
            throw new ArgumentException($"未知的表：{table}", nameof(table));
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>媒体条目总数；给定 sourceId 时只统计该源。</summary>
    public long CountMedia(string? sourceId = null)
    {
        if (TableNames().Contains("media", StringComparer.Ordinal) is false) return 0;

        using var cmd = _connection.CreateCommand();
        if (sourceId is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM media;";
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM media WHERE source_id = $s;";
            cmd.Parameters.AddWithValue("$s", sourceId);
        }

        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>当前真实存在的条目数（<c>status='ok'</c>）。missing/corrupt 不算。</summary>
    /// <remarks>
    /// 与 <see cref="CountMedia"/> 的区别是口径：管理页的"视频总数"要用这个，
    /// 否则文件夹搬走后数字不会变——missing 行是恢复锚点，留着但不算"库里有"。
    /// </remarks>
    public long CountAvailable(string? sourceId = null) => CountByStatus("ok", sourceId);

    /// <summary>缺失条目数（文件已不在扫描范围，行还挂着 meta/收藏/合集归属等恢复锚点）。</summary>
    public long CountMissing(string? sourceId = null) => CountByStatus("missing", sourceId);

    private long CountByStatus(string status, string? sourceId)
    {
        if (TableNames().Contains("media", StringComparer.Ordinal) is false) return 0;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sourceId is null
            ? "SELECT COUNT(*) FROM media WHERE status = $st;"
            : "SELECT COUNT(*) FROM media WHERE source_id = $s AND status = $st;";
        cmd.Parameters.AddWithValue("$st", status);
        if (sourceId is not null) cmd.Parameters.AddWithValue("$s", sourceId);

        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>媒体条目占用总字节数（只算 ok 条目，missing/corrupt 的文件已不在位）；
    /// 给定 sourceId 时只统计该源。</summary>
    public long TotalMediaBytes(string? sourceId = null)
    {
        if (TableNames().Contains("media", StringComparer.Ordinal) is false) return 0;

        using var cmd = _connection.CreateCommand();
        if (sourceId is null)
        {
            cmd.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM media WHERE status = 'ok';";
        }
        else
        {
            cmd.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM media WHERE source_id = $s AND status = 'ok';";
            cmd.Parameters.AddWithValue("$s", sourceId);
        }

        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private int ReadVersion()
    {
        var hasTable = QueryStrings(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';").Count > 0;
        if (!hasTable) return 0;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void UpsertVersion(int version)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO schema_version(version, name, applied_at) VALUES ($v, $n, $t) " +
            "ON CONFLICT(version) DO NOTHING;";
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$n", $"v{version}");
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private List<string> QueryStrings(string sql)
    {
        var list = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) list.Add(reader.GetString(0));
        }
        return list;
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>迁移结果，供 CLI 打印。</summary>
/// <param name="From">迁移前版本（0 = 空库）。</param>
/// <param name="To">迁移后版本。</param>
/// <param name="Applied">本次实际应用的版本序列。</param>
/// <param name="Changed">是否发生了变更。</param>
public sealed record MigrationResult(int From, int To, IReadOnlyList<int> Applied, bool Changed);
