using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MediaRack.Server.Hosting;

/// <summary>
/// 本机有哪些地址可以让人连进来。<b>唯一实现</b>——配对页、管理页、启动播报三处
/// 如果各写一份枚举逻辑，迟早出现"管理页能看到 Tailscale 地址、二维码里却没有"。
/// </summary>
public static class HostCatalog
{
    /// <summary>
    /// 地址类别。<see cref="Virtual"/> 就是 Tailscale/ZeroTier 那类 CGNAT 段——
    /// 在"手机在外网"这个最常见场景里，它往往是唯一通路，所以排序永远在第一。
    /// </summary>
    public enum Kind
    {
        Virtual,
        Lan,
        Other,
    }

    public sealed record HostInfo(string Address, Kind Kind);

    /// <summary>本机全部可用 IPv4，按"最可能连通"排序。<b>不含</b>环回与 APIPA。</summary>
    public static IReadOnlyList<HostInfo> Local()
    {
        var result = new List<HostInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(info.Address)) continue;

                var text = info.Address.ToString();
                // APIPA：DHCP 失败时的自配地址，连不上任何东西。
                if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                if (result.Any(r => r.Address == text)) continue;

                result.Add(new HostInfo(text, Classify(text)));
            }
        }

        return result.OrderBy(r => r.Kind).ThenBy(r => r.Address, StringComparer.Ordinal).ToList();
    }

    /// <summary>只要地址字符串（配对、二维码这些只需要候选列表的地方用）。</summary>
    public static IReadOnlyList<string> Addresses() => Local().Select(h => h.Address).ToList();

    /// <summary>
    /// 分类。注意 <c>100.64.0.0/10</c> 覆盖 100.64–100.127，
    /// 判断要按整段来而不是"以 100. 开头"——否则 100.128.x.x 这种真公网地址会被误判成 Tailscale。
    /// </summary>
    public static Kind Classify(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return Kind.Other;

        var b = ip.GetAddressBytes();
        if (b.Length != 4) return Kind.Other;

        // 100.64.0.0/10
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return Kind.Virtual;

        // 10.0.0.0/8
        if (b[0] == 10) return Kind.Lan;

        // 172.16.0.0/12
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return Kind.Lan;

        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return Kind.Lan;

        return Kind.Other;
    }

    public static string KindName(Kind kind) => kind switch
    {
        Kind.Virtual => "虚拟局域网",
        Kind.Lan => "内网",
        _ => "其他",
    };
}
