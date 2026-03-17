using ScreenshotOverlay.Services;
using ScreenshotOverlay.Models;

namespace ScreenshotOverlay;

public partial class App : System.Windows.Application
{
    private const double NewFrameOffset = 32;
    private SettingsService? _settingsService;
    private TrayIconService? _trayIconService;
    private readonly Dictionary<string, OverlayWindow> _frames = [];
    private int _nextFrameNumber = 1;
    private OverlayWindow? _hotkeyOwner;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _settingsService = new SettingsService();
        _trayIconService = new TrayIconService(AppIconFactory.CreateTrayIcon());
        _trayIconService.NewFrameRequested += (_, _) => CreateFrame();
        _trayIconService.ToggleFrameRequested += (_, id) => FocusFrame(id);
        _trayIconService.CaptureFrameRequested += (_, id) => CaptureFrame(id);
        _trayIconService.CloseFrameRequested += (_, id) => CloseFrame(id);
        _trayIconService.ExitRequested += (_, _) => CloseAllFramesAndExit();

        CreateFrame();
        _trayIconService.ShowBalloon("FrameSnip", "FrameSnip is running in the system tray.");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        _trayIconService = null;
        base.OnExit(e);
    }

    public void CreateNewFrameFromHotkey()
    {
        CreateFrame();
    }

    private void CreateFrame()
    {
        if (_settingsService is null)
        {
            return;
        }

        var frameId = Guid.NewGuid().ToString("N");
        var frameLabel = $"Frame {_nextFrameNumber++}";
        var settings = CreateFrameSettings();
        var ownsGlobalHotkeys = _frames.Count == 0;
        var window = new OverlayWindow(
            new CaptureService(),
            new ClipboardService(),
            new HotkeyService(),
            _settingsService,
            settings,
            frameLabel,
            ownsGlobalHotkeys);

        window.Icon = AppIconFactory.CreateWindowIconSource();
        window.Closed += (_, _) => HandleFrameClosed(frameId, window);

        _frames[frameId] = window;
        MainWindow ??= window;
        if (ownsGlobalHotkeys)
        {
            _hotkeyOwner = window;
        }

        window.Show();
        UpdateTrayFrames();
    }

    private AppSettings CreateFrameSettings()
    {
        var settings = _settingsService!.Load();
        var previousWindow = _frames.Values.LastOrDefault();

        if (previousWindow is null)
        {
            settings.StartInteractive = true;
            return settings;
        }

        var previousBounds = previousWindow.GetFrameSettingsSnapshot();

        return new AppSettings
        {
            Left = previousBounds.Left + NewFrameOffset,
            Top = previousBounds.Top + NewFrameOffset,
            Width = previousBounds.Width,
            Height = previousBounds.Height,
            StartInteractive = true
        };
    }

    private void HandleFrameClosed(string frameId, OverlayWindow window)
    {
        if (ReferenceEquals(_hotkeyOwner, window))
        {
            _hotkeyOwner = null;
        }

        _frames.Remove(frameId);
        if (ReferenceEquals(MainWindow, window))
        {
            MainWindow = _frames.Values.FirstOrDefault();
        }

        if (_hotkeyOwner is null && _frames.Count > 0)
        {
            SetHotkeyOwner(_frames.Values.Last());
        }

        UpdateTrayFrames();

        if (_frames.Count == 0)
        {
            Shutdown();
        }
    }

    private void CloseAllFramesAndExit()
    {
        foreach (var frame in _frames.Values.ToList())
        {
            frame.Close();
        }

        Shutdown();
    }

    public void SetHotkeyOwner(OverlayWindow window)
    {
        if (ReferenceEquals(_hotkeyOwner, window))
        {
            return;
        }

        _hotkeyOwner?.SetGlobalHotkeysEnabled(false);
        _hotkeyOwner = window;
        _hotkeyOwner.SetGlobalHotkeysEnabled(true);
    }

    private void FocusFrame(string frameId)
    {
        if (_frames.TryGetValue(frameId, out var window))
        {
            window.ShowOverlay();
        }
    }

    private void CaptureFrame(string frameId)
    {
        if (_frames.TryGetValue(frameId, out var window))
        {
            window.ShowOverlay();
            window.TriggerCapture();
        }
    }

    private void CloseFrame(string frameId)
    {
        if (_frames.TryGetValue(frameId, out var window))
        {
            window.Close();
        }
    }

    private void UpdateTrayFrames()
    {
        _trayIconService?.UpdateFrames(
            _frames.Select(frame => new TrayFrameItem
            {
                Id = frame.Key,
                Label = frame.Value.Title
            }).ToList());
    }
}
