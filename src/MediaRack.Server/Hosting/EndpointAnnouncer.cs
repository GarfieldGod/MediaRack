using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MediaRack.Server.Hosting;

/// <summary>
/// 启动时把可访问地址列出来，并按需打开浏览器。
///
/// 为什么要列全：绑定 <c>0.0.0.0</c> 之后真实地址不止一个——回环、局域网、VPN 各是不同 URL，
/// 而用户下一步正好要拿手机试。让他自己去 ipconfig 里找 IPv4 是没必要的摩擦。
/// </summary>
public static class EndpointAnnouncer
{
    public sealed record Endpoint(string Kind, string Url);

    /// <summary>把 Kestrel 实际监听的地址展开成"每一张网卡一条 URL"。</summary>
    public static IReadOnlyList<Endpoint> Expand(IEnumerable<string> listeningUrls)
    {
        var result = new List<Endpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in listeningUrls)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) continue;

            var port = uri.Port == 0 ? 8080 : uri.Port;

            // 本机回环永远给一条：不管绑的是 0.0.0.0 还是具体 IP，localhost 都该能开。
            Add(result, seen, "本机", $"http://127.0.0.1:{port}");

            if (uri.Host is "0.0.0.0" or "[::]" or "*" or "+")
            {
                foreach (var (name, address) in LocalIPv4())
                {
                    Add(result, seen, name, $"http://{address}:{port}");
                }
            }
            else if (uri.Host is not ("127.0.0.1" or "localhost"))
            {
                Add(result, seen, "指定地址", $"http://{uri.Host}:{port}");
            }
        }

        return result;
    }

    public static void Announce(IReadOnlyList<Endpoint> endpoints)
    {
        if (endpoints.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine("访问地址：");

        Print(endpoints, labelWidth: 0);

        Console.WriteLine("  同一 WiFi 下用手机打开上面的局域网地址即可；在外可走 VPN（如 Tailscale）那条。");
        Console.WriteLine();
    }

    /// <summary>一个对外服务的全部地址，用于多服务启动时的分组播报。</summary>
    public sealed record ServiceEndpoints(string Name, bool Locked, IReadOnlyList<Endpoint> Endpoints);

    /// <summary>
    /// 多服务模式下的播报：按服务分组，并标出哪个要密码。
    /// <para>
    /// 不分组的话，两个端口各列一串 IP，用户根本分不清哪条是公开的、哪条要密码——
    /// 而这恰好是他下一步要拿手机试的时候唯一需要知道的事。
    /// </para>
    /// </summary>
    public static void AnnounceServices(IReadOnlyList<ServiceEndpoints> services)
    {
        var all = services.Where(s => s.Endpoints.Count > 0).ToList();
        if (all.Count == 0) return;

        var nameWidth = all.Max(s => DisplayWidth(s.Name));
        var kindWidth = all.SelectMany(s => s.Endpoints).Max(e => DisplayWidth(e.Kind));

        Console.WriteLine();
        Console.WriteLine("访问地址：");

        foreach (var service in all)
        {
            var namePad = new string(' ', nameWidth - DisplayWidth(service.Name));
            var badge = service.Locked ? "  [需要密码]" : "  [公开]";
            Console.WriteLine($"  {service.Name}{namePad}{badge}");

            foreach (var endpoint in service.Endpoints)
            {
                var kindPad = new string(' ', kindWidth - DisplayWidth(endpoint.Kind));
                Console.WriteLine($"      {endpoint.Kind}{kindPad}  {endpoint.Url}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  同一 WiFi 下用手机打开上面的局域网地址即可；在外可走 VPN（如 Tailscale）那条。");
        Console.WriteLine();
    }

    private static void Print(IReadOnlyList<Endpoint> endpoints, int labelWidth)
    {
        // 按显示宽度对齐：中文字符占两列，string.PadRight 只数字符，
        // 混排"本机"和"Tailscale"会歪得很难看。
        var width = labelWidth > 0 ? labelWidth : endpoints.Max(e => DisplayWidth(e.Kind));
        foreach (var endpoint in endpoints)
        {
            var pad = new string(' ', width - DisplayWidth(endpoint.Kind));
            Console.WriteLine($"  {endpoint.Kind}{pad}  {endpoint.Url}");
        }
    }

    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text) width += ch >= '\u2E80' ? 2 : 1;
        return width;
    }

    /// <summary>打开默认浏览器。失败不该拖垮服务——顶多是用户自己点一下链接。</summary>
    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"打开浏览器失败：{ex.Message}");
            Console.Error.WriteLine($"请手动访问 {url}");
        }
    }

    /// <summary>有线/无线/VPN 各一张网卡的 IPv4。虚拟网卡也列出来——
    /// Tailscale 那种恰好是最有用的那条，过滤掉反而帮倒忙。</summary>
    private static IEnumerable<(string Kind, string Address)> LocalIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(info.Address)) continue;

                var text = info.Address.ToString();
                if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                // 网卡名直接用系统的（中文系统里会显示"以太网"），用户认得出来。
                yield return (nic.Name, text);
            }
        }
    }

    private static void Add(List<Endpoint> list, HashSet<string> seen, string kind, string url)
    {
        if (seen.Add(url)) list.Add(new Endpoint(kind, url));
    }
}
