using System.Security.Cryptography;
using System.Text;

namespace MediaRack.Models;

/// <summary>
/// 内容标识键。<b>一旦写入预取目录即不可更改</b>（见 docs/design.md §11）。
/// <para>
/// dual：<c>&lt;avid&gt;_c&lt;cid&gt;</c>；single：<c>s_&lt;sha1(relpath)[:16]&gt;</c>。
/// </para>
/// <para>
/// 只使用 <c>[0-9a-z_]</c>，因此可以直接放进 URL path 段，无需转义。
/// </para>
/// </summary>
public static class MediaKey
{
    public const string SinglePrefix = "s_";

    /// <summary>dual 源的键：<c>114053930288640_c28543681073</c>。</summary>
    public static string ForDual(long avid, long cid) => $"{avid}_c{cid}";

    /// <summary>dual 源的键（字符串形参，避免调用方先转 long）。</summary>
    public static string ForDual(string avid, string cid) => $"{avid}_c{cid}";

    /// <summary>
    /// single 源的键：相对路径的 SHA-1 前 16 位十六进制。
    /// 用<b>相对</b>路径，换盘符/换机器不失效；代价是同内容不同路径视为两条。
    /// </summary>
    public static string ForSingle(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = Normalize(relativePath);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(normalized));
        return SinglePrefix + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>拆分 dual 键。仅做形状校验，不验证数字合法性。</summary>
    public static bool TryParseDual(string key, out string avid, out string cid)
    {
        avid = cid = string.Empty;
        if (string.IsNullOrEmpty(key)) return false;

        var sep = key.LastIndexOf("_c", StringComparison.Ordinal);
        if (sep <= 0 || sep + 2 >= key.Length) return false;

        avid = key[..sep];
        cid = key[(sep + 2)..];
        return avid.All(char.IsAsciiDigit) && cid.All(char.IsAsciiDigit);
    }

    /// <summary>统一分隔符与大小写，保证同一文件在不同写法下得到同一个键。</summary>
    public static string Normalize(string relativePath)
    {
        var s = relativePath.Replace('\\', '/').Trim();
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        s = s.TrimStart('/');
        return s.ToLowerInvariant();
    }
}
