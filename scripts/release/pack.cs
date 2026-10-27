// 打 zip 的最小实现，供 publish.sh 在「系统没有 zip 命令」时兜底。
//
// 为什么需要它：Git for Windows 不自带 zip；tar -a 在 GNU tar 下产出的是
// 假 zip（把 tar 内容写进 .zip 文件名，unzip 直接报 "not a zipfile"）；
// 而发布脚本唯一能确定存在的东西就是 dotnet 本身。所以用 dotnet 的
// file-based app（.NET 10 的 `dotnet run xxx.cs`）来打，零外部依赖、跨平台一致。
//
// 用法：
//   dotnet run scripts/release/pack.cs -- <源目录> <输出.zip>
//
// includeBaseDirectory: false —— zip 根即目录内容（扁平结构，解压双击即用），
// 与之前 PowerShell 版 ZipFile.CreateFromDirectory 的语义保持一致。

using System.IO.Compression;

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: dotnet run scripts/release/pack.cs -- <源目录> <输出.zip>");
    return 2;
}

var src = Path.GetFullPath(args[0]);
var dst = Path.GetFullPath(args[1]);

if (!Directory.Exists(src))
{
    Console.Error.WriteLine($"目录不存在: {src}");
    return 2;
}

// 已存在就先删：ZipFile.CreateFromDirectory 遇到同名文件会抛异常，
// 而发布脚本的语义是「无条件覆盖上一次的包」。
if (File.Exists(dst)) File.Delete(dst);

ZipFile.CreateFromDirectory(src, dst, CompressionLevel.Optimal, includeBaseDirectory: false);

var mb = new FileInfo(dst).Length / 1048576.0;
Console.WriteLine($"{mb:F1} MB  {dst}");
return 0;
