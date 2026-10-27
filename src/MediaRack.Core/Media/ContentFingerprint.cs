using System.Security.Cryptography;

namespace MediaRack.Media;

/// <summary>
/// single 文件的内容指纹：文件大小 + 首尾各 64KB 的 SHA-256。
/// <para>
/// 目标不是密码学强度，而是"整理文件时认得出同一个视频"：移动/改名/换目录
/// 都不变；两份不同的视频几乎不可能大小相同且首尾 128KB 逐字节一致。
/// 全量哈希太贵（几个 GB 的文件读不动），首尾采样是成本与唯一性的折中。
/// </para>
/// </summary>
public static class ContentFingerprint
{
    private const int EdgeBytes = 64 * 1024;

    public static string Compute(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var size = fs.Length;

        using var sha = SHA256.Create();
        using var stream = new MemoryStream();

        // 大小先进 hash：只有首尾采样的两份不同大小文件不可能撞
        var sizePrefix = BitConverter.GetBytes(size);
        stream.Write(sizePrefix, 0, sizePrefix.Length);

        var head = new byte[Math.Min(size, EdgeBytes)];
        var headLen = fs.Read(head, 0, head.Length);
        stream.Write(head, 0, headLen);

        if (size > EdgeBytes)
        {
            fs.Seek(-EdgeBytes, SeekOrigin.End);
            var tail = new byte[EdgeBytes];
            var tailLen = fs.Read(tail, 0, tail.Length);
            stream.Write(tail, 0, tailLen);
        }

        stream.Position = 0;
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();   // 128bit 足够
    }
}
