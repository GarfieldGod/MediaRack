using Avalonia;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MediaRack.Client;

class Program
{
    /// <summary>
    /// <c>--autoplay [key]</c> 给定时，连上服务端后自动进播放页（验收 / 调试用）。
    /// 空串 = 播首屏第一条；null = 不自动播。
    /// </summary>
    public static string? AutoPlayKey { get; private set; }

    /// <summary>
    /// <c>--server http://host:port</c>：临时连这一台，不读也不写"上次用的服务端"。
    /// 验收 / 多环境调试用（本机跑一台开发实例时，不必把用户的连接记录改来改去）。
    /// </summary>
    public static string? StartupServer { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        Tools.ClientLog.Write($"进程启动，参数：{string.Join(" ", args)}");

        // 自检分支都往控制台打中文结论：不显式设 UTF-8 的话，
        // 一旦输出被管道接走就会按系统 ANSI 编码（GBK）编码，终端里全是乱码
        if (args.Any(a => a is "--selftest" or "--vmtest" or "--playtest" or "--connecttest"))
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 个别终端不支持 */ }
        }

        if (args.Contains("--server"))
        {
            var next = args.SkipWhile(a => a != "--server").Skip(1).FirstOrDefault();
            if (next is { Length: > 0 } && !next.StartsWith("--")) StartupServer = next;
        }

        if (args.Contains("--autoplay"))
        {
            var next = args.SkipWhile(a => a != "--autoplay").Skip(1).FirstOrDefault();
            AutoPlayKey = next is { Length: > 0 } && !next.StartsWith("--") ? next : "";
        }

        // 协议层自检：不初始化 Avalonia，跑完就退出（验收 / CI 用）
        //   MediaRack.Client --selftest [baseUrl]
        if (args.Contains("--selftest"))
        {
            var url = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault();
            return Task.Run(() => MediaRack.Client.Tools.SelfTest.RunAsync(
                string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:8080" : url!)).GetAwaiter().GetResult();
        }

        // 连接页无头验收（源地址必填 / 只接受门户地址 / 只能拿到一条源）
        //   MediaRack.Client --connecttest http://host:port/s/{slug} [token]
        if (args.Contains("--connecttest"))
        {
            var rest = args.SkipWhile(a => a != "--connecttest").Skip(1).Where(a => !a.StartsWith("--")).ToList();
            if (rest.Count == 0) return 1;
            return Task.Run(() => MediaRack.Client.Tools.ConnectTest.RunAsync(
                rest[0], rest.Count > 1 ? rest[1] : null)).GetAwaiter().GetResult();
        }

        // 主页 ViewModel 无头验收（切源 / 合集 / 搜索 / 续页 / 海报解码）
        //   MediaRack.Client --vmtest [baseUrl]
        if (args.Contains("--vmtest"))
        {
            var url = args.SkipWhile(a => a != "--vmtest").Skip(1).FirstOrDefault();

            // 解码 JPEG 要有渲染平台（Bitmap 走 Skia 的 IPlatformRenderInterface）。
            // 只要平台不要窗口：SetupWithoutStarting 不建窗口、不起消息循环，
            // 所以 vmtest 里那些 async/await 不会被 UI 线程卡住。
            try
            {
                BuildAvaloniaApp().SetupWithoutStarting();
            }
            catch (Exception e)
            {
                Tools.ClientLog.Write($"vmtest 平台初始化失败（海报解码会跳过）：{e.Message}");
            }

            return Task.Run(() => MediaRack.Client.Tools.VmTest.RunAsync(
                string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:8080" : url!)).GetAwaiter().GetResult();
        }

        // 播放链路无头验收（libvlc 起播 / 双流合成 / 进度写回 / 续播）
        //   MediaRack.Client --playtest [baseUrl]
        if (args.Contains("--playtest"))
        {
            var url = args.SkipWhile(a => a != "--playtest").Skip(1).FirstOrDefault();

            // 播放器要 Dispatcher（定时器），但不需要窗口：同样只升平台、不起消息循环
            try
            {
                BuildAvaloniaApp().SetupWithoutStarting();
            }
            catch (Exception e)
            {
                Tools.ClientLog.Write($"playtest 平台初始化失败：{e.Message}");
            }

            return Task.Run(() => MediaRack.Client.Tools.PlayTest.RunAsync(
                string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:8080" : url!)).GetAwaiter().GetResult();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            // GUI 进程没有控制台，启动期异常必须落到日志里，否则只看到"进程没了"
            Tools.ClientLog.Write($"启动失败：{e}");
            throw;
        }
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
