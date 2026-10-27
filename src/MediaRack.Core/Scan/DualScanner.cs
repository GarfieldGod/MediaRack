using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaRack.Models;

namespace MediaRack.Scan;

/// <summary>
/// B 站缓存（<c>layout=dual</c>）扫描器。
/// <para>
/// 只读：不创建、不修改、不删除源目录下的任何文件。
/// 完整性判据取自缓存自带的 <c>index.json</c>（size / md5），
/// 与 <c>entry.json.total_bytes</c> 互相印证。见 docs/probes-report.md。
/// </para>
/// </summary>
public sealed class DualScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly (int CodecId, string Name)[] CodecNames =
    [
        (7, "avc"),
        (12, "hevc"),
        (13, "av1"),
    ];

    /// <summary>扫描整棵源目录。</summary>
    /// <param name="progress">
    /// 逐条上报进度。为了能给出"一共多少条"，先把候选目录枚举成列表——
    /// 列目录相对读 entry.json / 算 md5 便宜得多，代价可以忽略。
    /// </param>
    public ScanResult Scan(
        SourceConfig source,
        VerifyMode verify,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Layout != SourceLayout.Dual)
        {
            throw new ArgumentException($"源 {source.Id} 不是 dual 布局", nameof(source));
        }

        var sw = Stopwatch.StartNew();
        var report = new ScanReport { SourceId = source.Id, Verify = verify };
        var entries = new List<MediaEntry>();

        // 源根在整次扫描里不变，取一次非空值，省得每个调用点都写 !
        var root = source.Root!;
        var itemDirs = EnumerateItemDirs(root, ct).ToList();
        for (var index = 0; index < itemDirs.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var itemDir = itemDirs[index];
            var relPath = Path.GetRelativePath(root, itemDir).Replace('\\', '/');

            progress?.Report(new ScanProgress(ScanStage.Scan, index + 1, itemDirs.Count, relPath));

            try
            {
                var scanned = ScanOne(itemDir, relPath, verify, source.Id);

                if (scanned.Entry is null)
                {
                    report.Problems.Add(new ScanProblem(relPath, scanned.Problem ?? "无法解析", Fatal: true));
                    continue;
                }

                entries.Add(scanned.Entry);
                report.Record(scanned.Entry, scanned.Problem is null
                    ? null
                    : new ScanProblem(relPath, scanned.Problem));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 单条目录坏掉不应中断整次扫描——这是"几万个条目里有一个是坏的"的常态。
                report.Problems.Add(new ScanProblem(relPath, ex.Message, Fatal: true));
            }
        }

        sw.Stop();
        report.Elapsed = sw.Elapsed;
        return new ScanResult(report, entries);
    }

    /// <summary>枚举 <c>&lt;root&gt;/&lt;avid&gt;/c_&lt;cid&gt;</c>；也兼容直接指向单个条目目录。</summary>
    private static IEnumerable<string> EnumerateItemDirs(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;

        // 允许直接指向 c_<cid> 目录（调试或单条搬运时很常见）
        if (File.Exists(Path.Combine(root, "entry.json")))
        {
            yield return root;
            yield break;
        }

        foreach (var avidDir in SafeEnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var cidDir in SafeEnumerateDirectories(avidDir))
            {
                ct.ThrowIfCancellationRequested();

                if (Path.GetFileName(cidDir).StartsWith("c_", StringComparison.OrdinalIgnoreCase))
                {
                    yield return cidDir;
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        string[] items;
        try
        {
            items = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var item in items)
        {
            var name = Path.GetFileName(item);
            if (name.StartsWith('.')) continue; // 跳过隐藏/元数据目录
            yield return item;
        }
    }

    private static ScannedItem ScanOne(string itemDir, string relPath, VerifyMode verify, string sourceId)
    {
        var entryFile = Path.Combine(itemDir, "entry.json");
        if (!File.Exists(entryFile))
        {
            return new ScannedItem(null, "缺少 entry.json");
        }

        var entryJson = JsonSerializer.Deserialize<EntryJson>(File.ReadAllText(entryFile), JsonOptions);
        if (entryJson?.Avid is null || entryJson.PageData?.Cid is null)
        {
            return new ScannedItem(null, "entry.json 缺少 avid 或 page_data.cid");
        }

        var avid = entryJson.Avid.Value;
        var cid = entryJson.PageData.Cid.Value;
        var key = MediaKey.ForDual(avid, cid);

        var tagDir = ResolveTagDir(itemDir, entryJson.TypeTag);
        if (tagDir is null)
        {
            return new ScannedItem(null, "找不到包含 m4s 的清晰度目录");
        }

        var problems = new List<string>();
        var videoPath = Path.Combine(tagDir, "video.m4s");
        var hasVideo = File.Exists(videoPath);
        var expectAudio = entryJson.HasDashAudio ?? true;
        var audioPath = Path.Combine(tagDir, "audio.m4s");
        var hasAudio = File.Exists(audioPath);

        var index = TryReadIndex(tagDir, problems);
        var videoStream = index?.Video?.FirstOrDefault();
        var audioStream = index?.Audio?.FirstOrDefault();

        MediaStatus status;
        if (!hasVideo && !hasAudio)
        {
            status = MediaStatus.Missing;
            problems.Add("video.m4s 与 audio.m4s 都不存在");
        }
        else if (!hasVideo)
        {
            status = MediaStatus.Corrupt;
            problems.Add("缺少 video.m4s");
        }
        else if (expectAudio && !hasAudio)
        {
            status = MediaStatus.Corrupt;
            problems.Add("entry.has_dash_audio 为真但缺少 audio.m4s");
        }
        else
        {
            status = MediaStatus.Ok;

            if (!VerifyFile(videoPath, videoStream?.Size, videoStream?.Md5, verify, "video.m4s", problems))
            {
                status = MediaStatus.Corrupt;
            }

            if (hasAudio && !VerifyFile(audioPath, audioStream?.Size, audioStream?.Md5, verify, "audio.m4s", problems))
            {
                status = MediaStatus.Corrupt;
            }
        }

        var tracks = new List<MediaTrack>(2);
        if (hasVideo)
        {
            tracks.Add(new MediaTrack
            {
                Role = TrackRole.Video,
                CodecId = videoStream?.CodecId,
                Bandwidth = videoStream?.Bandwidth,
                SizeBytes = videoStream?.Size ?? TryLength(videoPath),
                Md5 = videoStream?.Md5,
            });
        }

        if (hasAudio)
        {
            tracks.Add(new MediaTrack
            {
                Role = TrackRole.Audio,
                CodecId = audioStream?.CodecId,
                Bandwidth = audioStream?.Bandwidth,
                SizeBytes = audioStream?.Size ?? TryLength(audioPath),
                Md5 = audioStream?.Md5,
            });
        }

        var entry = new MediaEntry
        {
            SourceId = sourceId,
            Key = key,
            RelPath = relPath,
            RelDir = null,
            Layout = SourceLayout.Dual,
            TitleRaw = entryJson.Title,
            UpName = entryJson.OwnerName,
            UpMid = entryJson.OwnerId?.ToString(),
            DurationMs = entryJson.TotalTimeMilli,
            Width = entryJson.PageData.Width ?? videoStream?.Width,
            Height = entryJson.PageData.Height ?? videoStream?.Height,
            Codec = MapCodec(videoStream?.CodecId),
            TagCode = ParseTag(entryJson.TypeTag),
            SizeBytes = entryJson.TotalBytes ?? SumTracks(tracks),
            HasDanmaku = File.Exists(Path.Combine(itemDir, "danmaku.xml")),
            Status = status,
            ScannedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Tracks = tracks,
        };

        return new ScannedItem(entry, problems.Count == 0 ? null : string.Join("；", problems));
    }

    /// <summary>优先用 entry.type_tag，缺失时退化为"第一个含 video.m4s 的子目录"。</summary>
    private static string? ResolveTagDir(string itemDir, string? typeTag)
    {
        if (!string.IsNullOrWhiteSpace(typeTag))
        {
            var candidate = Path.Combine(itemDir, typeTag);
            if (Directory.Exists(candidate)) return candidate;
        }

        foreach (var sub in SafeEnumerateDirectories(itemDir))
        {
            if (File.Exists(Path.Combine(sub, "video.m4s")) || File.Exists(Path.Combine(sub, "audio.m4s")))
            {
                return sub;
            }
        }

        return null;
    }

    private static IndexJson? TryReadIndex(string tagDir, List<string> problems)
    {
        var path = Path.Combine(tagDir, "index.json");
        if (!File.Exists(path))
        {
            // 老版本缓存可能没有 index.json：仍可入库，只是失去完整性依据。
            problems.Add("缺少 index.json，跳过完整性校验");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IndexJson>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            problems.Add($"index.json 解析失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>按 <see cref="VerifyMode"/> 校验单个 m4s。返回 false 表示判定为损坏。</summary>
    private static bool VerifyFile(
        string path, long? expectedSize, string? expectedMd5,
        VerifyMode verify, string label, List<string> problems)
    {
        if (verify == VerifyMode.None) return true;

        var actualLength = new FileInfo(path).Length;

        if (expectedSize is { } size && size != actualLength)
        {
            problems.Add($"{label} 大小不符（期望 {size}，实际 {actualLength}）");
            return false;
        }

        if (verify != VerifyMode.Full || string.IsNullOrWhiteSpace(expectedMd5)) return true;

        using var stream = File.OpenRead(path);
        var actualMd5 = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();

        if (!string.Equals(actualMd5, expectedMd5.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            problems.Add($"{label} md5 不符（期望 {expectedMd5}，实际 {actualMd5}）");
            return false;
        }

        return true;
    }

    private static long? TryLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static long? SumTracks(List<MediaTrack> tracks)
    {
        var sum = tracks.Sum(t => t.SizeBytes ?? 0);
        return sum > 0 ? sum : null;
    }

    private static string? MapCodec(int? codecId) =>
        CodecNames.FirstOrDefault(c => c.CodecId == codecId).Name;

    private static int? ParseTag(string? typeTag) =>
        int.TryParse(typeTag, out var tag) ? tag : null;

    private sealed record ScannedItem(MediaEntry? Entry, string? Problem);

    // ── 缓存 JSON 的镜像（字段名与 B 站客户端保持一致）─────────────
    private sealed class EntryJson
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("avid")] public long? Avid { get; set; }
        [JsonPropertyName("bvid")] public string? Bvid { get; set; }
        [JsonPropertyName("type_tag")] public string? TypeTag { get; set; }
        [JsonPropertyName("total_time_milli")] public int? TotalTimeMilli { get; set; }
        [JsonPropertyName("total_bytes")] public long? TotalBytes { get; set; }
        [JsonPropertyName("has_dash_audio")] public bool? HasDashAudio { get; set; }
        [JsonPropertyName("is_completed")] public bool? IsCompleted { get; set; }
        [JsonPropertyName("owner_name")] public string? OwnerName { get; set; }
        [JsonPropertyName("owner_id")] public long? OwnerId { get; set; }
        [JsonPropertyName("page_data")] public PageDataJson? PageData { get; set; }
    }

    private sealed class PageDataJson
    {
        [JsonPropertyName("cid")] public long? Cid { get; set; }
        [JsonPropertyName("page")] public int? Page { get; set; }
        [JsonPropertyName("part")] public string? Part { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
        [JsonPropertyName("rotate")] public int? Rotate { get; set; }
    }

    private sealed class IndexJson
    {
        [JsonPropertyName("video")] public List<StreamJson>? Video { get; set; }
        [JsonPropertyName("audio")] public List<StreamJson>? Audio { get; set; }
    }

    private sealed class StreamJson
    {
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("md5")] public string? Md5 { get; set; }
        [JsonPropertyName("bandwidth")] public long? Bandwidth { get; set; }
        [JsonPropertyName("codecid")] public int? CodecId { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
    }
}
