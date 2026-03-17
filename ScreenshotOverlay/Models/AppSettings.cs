namespace ScreenshotOverlay.Models;

public sealed class AppSettings
{
    public double Left { get; set; } = 120;
    public double Top { get; set; } = 120;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 260;
    public bool StartInteractive { get; set; } = true;
}
