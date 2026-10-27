using MediaRack.Library;

namespace MediaRack.Server.Hosting;

/// <summary>
/// 服务端配置。全部可从 <c>appsettings.json</c> 的 <c>MediaRack</c> 节读取，
/// 其中 <c>--meta</c> 与 <c>--token</c> 支持命令行覆盖（命令行优先）。
/// </summary>
public sealed record ServerOptions
{
    /// <summary>元数据目录（库 + 封面 + 导出产物都在这里）。</summary>
    public required string MetaRoot { get; init; }

    /// <summary>
    /// 访问令牌。为空表示不校验——局域网自用场景下这是默认值。
    /// 一旦配了，除静态页与健康检查外所有请求都要带 <c>Authorization: Bearer</c>。
    /// </summary>
    public string? Token { get; init; }

    /// <summary>导出产物总预算，超出后按最久未使用淘汰。</summary>
    public long ExportBudgetBytes { get; init; } = ExportService.DefaultBudgetBytes;

    /// <summary>首帧缩略图总预算，超出后按最久未使用淘汰。</summary>
    public long ThumbBudgetBytes { get; init; } = ThumbService.DefaultBudgetBytes;

    /// <summary>缩略图最大宽度（不放大）。</summary>
    public int PosterWidth { get; init; } = 560;

    /// <summary>
    /// 启动后自动打开浏览器（<c>--open</c>）。默认关闭：
    /// 装成服务或跑测试时弹出窗口是纯粹的干扰。
    /// </summary>
    public bool Open { get; init; }

    /// <summary>
    /// 传统模式（没有 config.json）下的监听端口，从 <c>--urls</c> 推导。
    /// <para>
    /// 只用来生成 cookie 名与日志，真正的绑定仍由 Kestrel 按 urls 完成。
    /// 多服务模式下端口来自 config.json，这个值被忽略。
    /// </para>
    /// </summary>
    public int Port { get; init; } = 8080;

    /// <summary>默认库位置：<c>%LOCALAPPDATA%\MediaRack</c>。</summary>
    public static string DefaultMetaRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRack");

    /// <summary>
    /// 解析配置。优先级：命令行 &gt; <c>config.json</c> &gt; <c>appsettings.json</c> &gt; 内置默认。
    /// <para>
    /// 命令行排第一是因为它专用于"这次先这么跑"，覆盖文件是它的本分；
    /// config.json 排在 appsettings 之前，是因为 appsettings 随程序发布、用户不该去改它。
    /// </para>
    /// </summary>
    public static ServerOptions From(IConfiguration config, string[] args, MediaRackConfig? fileConfig = null)
    {
        var section = config.GetSection("MediaRack");

        // 空串与"没配"等价：appsettings 里留一个空字符串比删掉整行更常见，
        // 直接 Path.GetFullPath("") 会把库建到当前工作目录，那是最坏的结果。
        var metaRoot = ArgValue(args, "--meta");
        if (string.IsNullOrWhiteSpace(metaRoot)) metaRoot = fileConfig?.MetaRoot;
        if (string.IsNullOrWhiteSpace(metaRoot)) metaRoot = section["MetaRoot"];
        if (string.IsNullOrWhiteSpace(metaRoot)) metaRoot = DefaultMetaRoot;

        var token = ArgValue(args, "--token");
        if (string.IsNullOrWhiteSpace(token)) token = section["Token"];

        var budget = ParseLong(section["ExportBudgetBytes"], ExportService.DefaultBudgetBytes);
        var posterWidth = (int)ParseLong(section["PosterWidth"], 560);

        return new ServerOptions
        {
            MetaRoot = Path.GetFullPath(metaRoot),
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            ExportBudgetBytes = budget,
            PosterWidth = posterWidth,
            Open = ArgFlag(args, "--open") || section.GetValue("Open", false),
            Port = ParsePort(config, args),
        };
    }

    /// <summary>
    /// 从 <c>--urls</c> / <c>ASPNETCORE_URLS</c> 里取第一个 http(s) 端点的端口。
    /// 拿不到就返回 8080（本项目的惯用端口），不猜 5000。
    /// </summary>
    private static int ParsePort(IConfiguration config, string[] args)
    {
        var urls = ArgValue(args, "--urls") ?? config["urls"] ?? config["ASPNETCORE_URLS"];
        if (string.IsNullOrWhiteSpace(urls)) return 8080;

        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // Kestrel 允许 http://*:8080 与 http://+:8080 这两种通配写法，Uri 解析不了，先换成占位主机。
            var candidate = raw.Trim().Replace("*", "0.0.0.0").Replace("+", "0.0.0.0");
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https")) continue;
            return uri.Port;
        }

        return 8080;
    }

    /// <summary>无值开关。也接受 <c>--open=true</c> 这种写法。</summary>
    private static bool ArgFlag(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;

            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) &&
                bool.TryParse(arg[(name.Length + 1)..], out var value))
            {
                return value;
            }
        }

        return false;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        // 也接受 --meta=value 的形式。
        foreach (var arg in args)
        {
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..];
            }
        }

        return null;
    }

    private static long ParseLong(string? value, long fallback) =>
        long.TryParse(value, out var n) && n > 0 ? n : fallback;
}
