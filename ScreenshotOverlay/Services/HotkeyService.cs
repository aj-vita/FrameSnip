using ScreenshotOverlay.Interop;

namespace ScreenshotOverlay.Services;

public sealed class HotkeyService
{
    public const int ToggleModeHotkeyId = 9001;
    public const int CaptureHotkeyId = 9002;
    public const int ExitHotkeyId = 9003;

    private IntPtr _windowHandle = IntPtr.Zero;

    public void Register(IntPtr windowHandle)
    {
        if (_windowHandle == windowHandle)
        {
            return;
        }

        if (_windowHandle != IntPtr.Zero)
        {
            Unregister();
        }

        _windowHandle = windowHandle;

        RegisterOrThrow(ToggleModeHotkeyId, NativeMethods.ModControl | NativeMethods.ModShift, (uint)System.Windows.Forms.Keys.Space);
        RegisterOrThrow(CaptureHotkeyId, NativeMethods.ModControl | NativeMethods.ModShift, (uint)System.Windows.Forms.Keys.S);
        RegisterOrThrow(ExitHotkeyId, NativeMethods.ModControl | NativeMethods.ModShift, (uint)System.Windows.Forms.Keys.Q);
    }

    public void Unregister()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_windowHandle, ToggleModeHotkeyId);
        NativeMethods.UnregisterHotKey(_windowHandle, CaptureHotkeyId);
        NativeMethods.UnregisterHotKey(_windowHandle, ExitHotkeyId);
        _windowHandle = IntPtr.Zero;
    }

    private void RegisterOrThrow(int id, uint modifiers, uint key)
    {
        if (!NativeMethods.RegisterHotKey(_windowHandle, id, modifiers, key))
        {
            throw new InvalidOperationException($"Could not register hotkey {id}. It may already be in use.");
        }
    }
}
