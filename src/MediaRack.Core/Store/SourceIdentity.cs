using System.Security.Cryptography;
using System.Text;

namespace MediaRack.Store;

/// <summary>
/// 源身份：URL 段（slug）与访问令牌的生成规则。<b>唯一实现</b>——
/// 迁移补齐、新建源、改名三处都走这里，否则同一个源会有两种 slug 风格。
/// </summary>
public static class SourceIdentity
{
    /// <summary>slug 的最大长度。URL 段太长在日志和手机上都难读。</summary>
    public const int MaxSlugLength = 32;

    /// <summary>
    /// 手写令牌的建议长度。低于它不拦（用户有权用短令牌），只在上层给提示。
    /// </summary>
    public const int SuggestedTokenLength = 16;

    /// <summary>
    /// 把任意文本压成 URL 段：只保留 <c>a-z0-9</c>，其余折叠成一个 <c>-</c>。
    /// <para>
    /// 中文目录名会压成空串——这是有意的：<c>/s/视频</c> 在 URL 里要百分号编码，
    /// 复制、粘贴、日志排查三处都难看。空串由调用方回退成 <c>s_xxxxxxxx</c>。
    /// </para>
    /// </summary>
    public static string Slugify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new StringBuilder(MaxSlugLength);
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }

            if (sb.Length >= MaxSlugLength) break;
        }

        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// 稳定回退：由源 id 派生出 <c>s_xxxxxxxx</c>。
    /// 用哈希而不是随机串，是为了让"删了库重建"得到同一个 slug，客户端配置不会莫名其妙失效。
    /// </summary>
    public static string FallbackSlug(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        var sb = new StringBuilder("s_", 10);
        foreach (var b in hash)
        {
            sb.Append((char)('a' + (b % 26)));
            if (sb.Length >= 10) break;
        }

        return sb.ToString();
    }

    /// <summary>由 id 派生候选 slug（可能仍是空，调用方再回退 <see cref="FallbackSlug"/>）。</summary>
    public static string CandidateSlug(string id, string? name = null)
    {
        var fromName = Slugify(name);
        if (fromName.Length > 0) return fromName;

        var fromId = Slugify(id);
        return fromId.Length > 0 ? fromId : FallbackSlug(id);
    }

    /// <summary>slug 是否合法（用于手改写入口）。</summary>
    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) &&
        slug.Length <= MaxSlugLength &&
        char.IsAsciiLetterOrDigit(slug[0]) &&
        slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>生成一个 22 字符的令牌（约 132 bit）。</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
