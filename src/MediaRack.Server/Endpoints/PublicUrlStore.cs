using MediaRack.Server.Hosting;

namespace MediaRack.Server.Endpoints;

/// <summary>
/// 公网地址（config.json 的 <c>publicUrl</c>）的读取。管理页与配对二维码共用，
/// 两处各写一份迟早出现"管理页显示 A、二维码里装的是 B"。
/// </summary>
internal static class PublicUrlStore
{
    /// <summary>
    /// 公网地址只能手填。<b>不要</b>试图自动探测：本机看不到路由器的端口映射，
    /// 也看不到 NAT 出口，猜一个 IP 填上去的结果是"看起来配好了、实际打不通"。
    /// </summary>
    public static string? Read(ServiceCatalog catalog)
    {
        var path = catalog.ConfigPath;
        if (string.IsNullOrEmpty(path) || path == "<testing>") return null;

        try
        {
            return MediaRackConfig.Load(path).PublicUrl;
        }
        catch
        {
            return null;
        }
    }
}
