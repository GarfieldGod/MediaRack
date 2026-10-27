using System.Text;
using MediaRack.Server.Hosting;

// 中文网卡名（"以太网"）在默认代码页下会变成乱码。服务端基本只在控制台看输出，
// 这一步是给"访问地址"那几行用的。
try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* 输出被重定向时无控制台可设 */ }

// 管线全部在 MediaRackHost 里：命令行与桌面托盘壳必须跑同一套逻辑。
MediaRackHost.Build(args).Run();

/// <summary>
/// 让测试项目能用 <c>WebApplicationFactory&lt;Program&gt;</c> 启动真实管线。
/// top-level statements 生成的 Program 是 internal，不这样声明就引用不到。
/// </summary>
public partial class Program;
