using MediaRack.Models;

namespace MediaRack.Scan;

/// <summary>一次扫描的结果。CLI 与测试都从这里取数。</summary>
public sealed class ScanReport
{
    public required string SourceId { get; init; }
    public required VerifyMode Verify { get; init; }

    public int OkCount { get; set; }
    public int CorruptCount { get; set; }
    public int MissingCount { get; set; }

    /// <summary>无法解析的目录（坏 JSON、缺 entry.json 等）。不计入成功也不阻断其余条目。</summary>
    public List<ScanProblem> Problems { get; } = [];

    public int TotalScanned => OkCount + CorruptCount + MissingCount;

    public TimeSpan Elapsed { get; set; }

    public int Upserted { get; set; }

    /// <summary>本次扫描未出现、已被标记为 missing 的既有条目数。</summary>
    public int MarkedMissing { get; set; }

    public void Record(MediaEntry entry, ScanProblem? problem = null)
    {
        switch (entry.Status)
        {
            case MediaStatus.Ok: OkCount++; break;
            case MediaStatus.Corrupt: CorruptCount++; break;
            case MediaStatus.Missing: MissingCount++; break;
        }

        if (problem is not null) Problems.Add(problem);
    }
}

/// <summary>单个条目的问题记录。</summary>
/// <param name="RelPath">相对源根目录的位置，便于用户直接定位。</param>
/// <param name="Message">人类可读的原因。</param>
/// <param name="Fatal">true 表示整个条目无法入库（如 entry.json 解析失败）。</param>
public sealed record ScanProblem(string RelPath, string Message, bool Fatal = false);
