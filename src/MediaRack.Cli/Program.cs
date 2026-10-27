using System.Text;
using MediaRack.Cli;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // 输出被重定向时某些宿主不允许改编码，忽略即可。
}

return CliApp.Run(args);
