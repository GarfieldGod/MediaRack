namespace MediaRack.Store;

/// <summary>
/// 一次合集重建的结果，进 <c>ScanOutcome</c> 与审计报告。
/// <para>
/// v4 起合集不再"逐目录同步"，而是整库重建（<see cref="CollectionRebuilder"/>）：
/// <see cref="Collections"/> 是重建后的合集总数，<see cref="Items"/> 是绑定到合集的条目数。
/// </para>
/// </summary>
public sealed record CollectionSyncResult(
    int Collections,
    int Items,
    int RemovedCollections = 0,
    int RemovedItems = 0);
