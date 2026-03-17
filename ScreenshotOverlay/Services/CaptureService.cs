using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenshotOverlay.Interop;

namespace ScreenshotOverlay.Services;

public sealed class CaptureService
{
    public BitmapSource Capture(Int32Rect bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.CopyFromScreen(
            bounds.X,
            bounds.Y,
            0,
            0,
            new System.Drawing.Size(bounds.Width, bounds.Height),
            CopyPixelOperation.SourceCopy);

        var hBitmap = bitmap.GetHbitmap();

        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }
}
