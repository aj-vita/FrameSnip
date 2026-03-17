using System.Runtime.InteropServices;

namespace ScreenshotOverlay.Interop;

internal static class NativeMethods
{
    private const int MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public const int GwlExStyle = -20;
    public const int WsExLayered = 0x80000;
    public const int WmHotKey = 0x0312;
    public const int WmNcHitTest = 0x0084;
    public const int HtTransparent = -1;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public const uint ModShift = 0x0004;
    public const uint ModControl = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointStruct lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectStruct lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointStruct pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static System.Windows.Point GetCursorScreenPosition()
    {
        GetCursorPos(out var point);
        return new System.Windows.Point(point.X, point.Y);
    }

    public static System.Windows.Int32Rect GetWindowRectangle(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var rect))
        {
            throw new InvalidOperationException("Could not read window bounds.");
        }

        return new System.Windows.Int32Rect(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    public static System.Windows.Int32Rect GetVirtualScreenRectangle()
    {
        return new System.Windows.Int32Rect(
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen),
            GetSystemMetrics(SmCyVirtualScreen));
    }

    public static (double ScaleX, double ScaleY) GetScaleForScreenPoint(int x, int y)
    {
        var point = new PointStruct { X = x, Y = y };
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return (1d, 1d);
        }

        var result = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out var dpiY);
        if (result != 0)
        {
            return (1d, 1d);
        }

        return (dpiX / 96d, dpiY / 96d);
    }
}
