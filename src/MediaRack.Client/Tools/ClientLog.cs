namespace MediaRack.Client.Tools;

/// <summary>
/// 客户端运行日志（<c>%LOCALAPPDATA%/MediaRack/logs/client.log</c>，追加写、单行一条）。
///
/// GUI 程序没有控制台，自动重连这类"窗口起来了但什么都没发生"的问题靠猜是猜不出来的，
/// 必须留一条可查的线索。写失败不影响主流程。
/// </summary>
public static class ClientLog
{
    private static readonly object Gate = new();

    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaRack", "logs", "client.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不了不能影响使用
        }
    }
}
