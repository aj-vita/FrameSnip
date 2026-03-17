using System.Windows.Media.Imaging;

namespace ScreenshotOverlay.Services;

public sealed class ClipboardService
{
    public void Copy(BitmapSource image)
    {
        System.Windows.Clipboard.SetImage(image);
    }
}
