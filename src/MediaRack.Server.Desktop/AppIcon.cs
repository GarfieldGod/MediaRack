using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace MediaRack.Server.Desktop;

/// <summary>
/// 程序内画一个托盘图标，不额外带 .ico 文件。
/// 这样单文件发布时不用操心资源路径，也不会出现"图标丢了变成白方块"。
/// </summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(Color.FromArgb(0x4F, 0x63, 0xE0));
            g.FillEllipse(brush, 1, 1, 29, 29);

            // 一个播放三角形的负形，比字母 M 更像"播放器"。
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white, [new PointF(12, 9), new PointF(23, 16), new PointF(12, 23)]);
        }

        var handle = bitmap.GetHicon();

        try
        {
            // Clone 之后 Icon 不再依赖这个 HICON，可以安全销毁原始句柄。
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
