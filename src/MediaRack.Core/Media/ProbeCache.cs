using MediaRack.Store;

namespace MediaRack.Media;

/// <summary>
/// single 源的 ffprobe 结果缓存（按库预载，扫描期只读）。
/// <para>
/// 键 = 文件绝对路径（大小写不敏感，Windows 文件系统语义）。判变依据 = 库里
/// 上次记录的 file_mtime + size_bytes 与本次 stat 一致——两个都没变就认定
/// 媒体内容未变，直接回放库里的时长/分辨率/编码，不 spawning ffprobe 子进程。
/// </para>
/// <para>
/// 只缓存 status='ok' 且有 probe 数据的行：损坏/0 字节的行本来就没有 probe 值，
/// 留着它们只会造成"伪命中"。
/// </para>
/// </summary>
public sealed class ProbeCache
{
    private readonly Dictionary<string, (long Mtime, long Size, ProbeResult Result)> _byPath;

    private ProbeCache(Dictionary<string, (long, long, ProbeResult)> byPath) => _byPath = byPath;

    /// <summary>预载的缓存条数（诊断用）。</summary>
    public int Count => _byPath.Count;

    /// <summary>从库预载某源在某个文件夹根下的 probe 缓存。表不存在（老库）时返回空缓存。</summary>
    public static ProbeCache Load(LibraryDatabase db, string sourceId, string folderRoot)
    {
        var byPath = new Dictionary<string, (long, long, ProbeResult)>(StringComparer.OrdinalIgnoreCase);

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT rel_path, size_bytes, file_mtime, duration_ms, width, height, codec
            FROM media
            WHERE source_id = $sid AND layout = 'single' AND status = 'ok'
              AND file_mtime IS NOT NULL AND size_bytes IS NOT NULL
              AND duration_ms IS NOT NULL AND width IS NOT NULL AND height IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$sid", sourceId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var relPath = reader.GetString(0);
            var size = reader.GetInt64(1);
            var mtime = reader.GetInt64(2);

            // ProbeResult.Rotation 没有单独落库（宽高已按旋转交换过），缓存回放时置 0。
            var result = new ProbeResult(
                reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5),
                Rotation: 0, reader.IsDBNull(6) ? null : reader.GetString(6), HasAudio: true);

            var absolute = Path.GetFullPath(Path.Combine(folderRoot, relPath));
            byPath[absolute] = (mtime, size, result);
        }

        return new ProbeCache(byPath);
    }

    /// <summary>mtime + size 都与库记录一致才命中。</summary>
    public bool TryGet(string absolutePath, long currentSize, long currentMtime, out ProbeResult result)
    {
        result = null!;
        if (!_byPath.TryGetValue(absolutePath, out var hit)) return false;
        if (hit.Size != currentSize || hit.Mtime != currentMtime) return false;
        result = hit.Result;
        return true;
    }
}

/// <summary>
/// <see cref="IMediaProbe"/> 装饰器：先查 <see cref="ProbeCache"/>，未命中才落到底层
/// （通常是 ffprobe）。扫描器与缓存完全解耦——测试注入假 probe 时行为不变。
/// </summary>
public sealed class CachedProbe(IMediaProbe inner, ProbeCache cache) : IMediaProbe
{
    public bool TryProbe(string path, out ProbeResult result, out string? error)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception)
        {
            // stat 都失败的路径交给底层走它自己的错误通道。
            return inner.TryProbe(path, out result, out error);
        }

        if (info.Exists && cache.TryGet(
                Path.GetFullPath(path), info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(), out var cached))
        {
            result = cached;
            error = null;
            return true;
        }

        return inner.TryProbe(path, out result, out error);
    }
}
