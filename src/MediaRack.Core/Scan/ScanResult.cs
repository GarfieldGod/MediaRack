using MediaRack.Models;

namespace MediaRack.Scan;

/// <summary>扫描产物：报告 + 待写入的条目。两种布局共用同一个形状，CLI 无需分支。</summary>
public sealed record ScanResult(ScanReport Report, IReadOnlyList<MediaEntry> Entries);
