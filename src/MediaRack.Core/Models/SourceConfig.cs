namespace MediaRack.Models;

/// <summary>一个已登记的源。只读：本类不含任何指向磁盘的写操作。</summary>
public sealed class SourceConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// 首文件夹的布局（<see cref="Folders"/> 的投影）。
    /// <b>新代码不要读这个判断多文件夹源</b>——布局跟文件夹走，见 <see cref="Folders"/>。
    /// </summary>
    public SourceLayout Layout { get; init; } = SourceLayout.Single;

    /// <summary>
    /// 遗留投影 = <see cref="Folders"/> 首项的根目录（source 表 root 列，v3 起不再作为真相）。
    /// 保留是因为扫描作业与 CLI 构造"单文件夹视角"的 SourceConfig 时仍直接给它赋值。
    /// </summary>
    public string? Root { get; init; }

    /// <summary>
    /// 源名下的内容文件夹列表（v3）。按 <see cref="SourceFolder.Position"/> 排序；
    /// 空列表 = 刚建的源还没登记文件夹，库里没有内容可扫。
    /// 存储层组装时填充，故可写。
    /// </summary>
    public IReadOnlyList<SourceFolder> Folders { get; set; } = [];

    /// <summary>
    /// 对外 URL 段：<c>/s/{Slug}</c>。默认从目录名派生，用户可改。
    /// <para>
    /// 与 <see cref="Id"/> 分开的理由：id 来自 <c>Path.GetFileName(root)</c>，可能是中文或含空格，
    /// 而 URL 段必须是 ASCII。id 又是四张表的外键前缀，改它要级联，所以只给 slug 单独一列。
    /// </para>
    /// </summary>
    public string? Slug { get; init; }

    /// <summary>
    /// 该源的访问令牌。为空表示"还没分配"，由 <c>SourceRepository.EnsureIdentity</c> 补齐。
    /// <b>不设 required</b>：扫描作业构造 SourceConfig 时不带身份，upsert 必须保留库里已有的值。
    /// </summary>
    public string? Token { get; init; }

    /// <summary>虚拟写权限。无论取何值，磁盘都不会被写入。</summary>
    public VirtualWrite VirtualWrite { get; init; } = VirtualWrite.Full;

    public bool Enabled { get; init; } = true;

    /// <summary>可选的扫描过滤通配（如 <c>*.mp4;*.mkv</c>）。</summary>
    public string? ScanInclude { get; init; }

    public long? LastScanAt { get; init; }
    public int? LastCount { get; init; }
}
