using System.Drawing;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenshotOverlay.Interop;

namespace ScreenshotOverlay.Services;

public static class AppIconFactory
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (extracted is not null)
            {
                return (System.Drawing.Icon)extracted.Clone();
            }
        }

        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var pen = new Pen(Color.FromArgb(72, 72, 72), 10f);
        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

        graphics.DrawLine(pen, 12, 20, 44, 20);
        graphics.DrawLine(pen, 20, 12, 20, 20);
        graphics.DrawArc(pen, 44, 20, 12, 12, 270, 90);
        graphics.DrawLine(pen, 56, 26, 56, 56);
        graphics.DrawLine(pen, 20, 28, 20, 44);
        graphics.DrawArc(pen, 20, 44, 12, 12, 180, 90);
        graphics.DrawLine(pen, 26, 56, 40, 56);
        graphics.DrawLine(pen, 56, 56, 68 - 12, 56);

        var handle = bitmap.GetHicon();

        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public static BitmapSource CreateWindowIconSource()
    {
        using var icon = CreateTrayIcon();
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(64, 64));
        source.Freeze();
        return source;
    }
}
