namespace MediaRack.Scan;

/// <summary>扫描时的完整性校验强度。</summary>
public enum VerifyMode
{
    /// <summary>只检查文件是否存在。最快，适合首次建库。</summary>
    None,

    /// <summary>存在性 + 字节数与 index.json 记录一致。默认。</summary>
    Sample,

    /// <summary>存在性 + 字节数 + md5 全量比对。慢，用于体检或排障。</summary>
    Full,
}
