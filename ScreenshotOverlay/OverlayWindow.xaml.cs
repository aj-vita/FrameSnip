using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenshotOverlay.Interop;
using ScreenshotOverlay.Models;
using ScreenshotOverlay.Services;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenshotOverlay;

public partial class OverlayWindow : Window
{
    private const double AnnotationToolbarHeight = 34;
    private const double AnnotationToolbarWidth = 540;
    private const double CompactStatusBadgeWidth = 64;
    private const double ExpandedActiveStatusBadgeWidth = 420;
    private const double ExpandedInactiveStatusBadgeWidth = 340;
    private const double StatusBadgeHorizontalMargin = 32;
    private const double StatusBadgeRightSafetyGap = 18;
    private const double StatusBadgeLeftInsetWithinFrame = 14;
    private const double StatusBadgeReservedRightZone = 92;
    private const double FrameInteractionPadding = 18;
    private const double ResizeActivationMargin = 18;
    private enum AnnotationTool
    {
        Pen,
        Highlighter,
        Text,
        Eraser,
        Rectangle,
        Redact,
    }

    private enum AnnotationActionKind
    {
        Stroke,
        Element,
    }

    private sealed class AnnotationAction
    {
        public required AnnotationActionKind Kind { get; init; }
        public Stroke? Stroke { get; init; }
        public FrameworkElement? Element { get; init; }
    }

    private sealed class TextAnnotationVisual
    {
        public required Grid Container { get; init; }
        public required Border Outline { get; init; }
        public required System.Windows.Controls.TextBox Editor { get; init; }
        public required Thumb ResizeThumb { get; init; }
    }

    private const double MinimumFrameWidth = 160;
    private const double MinimumFrameHeight = 120;
    private const double SnapThreshold = 12;

    private readonly CaptureService _captureService;
    private readonly ClipboardService _clipboardService;
    private readonly HotkeyService _hotkeyService;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _hoverTimer;
    private readonly bool _initiallyOwnsGlobalHotkeys;

    private bool _isInteractive = true;
    private bool _isAnnotating;
    private bool _isConventionalAnnotationSession;
    private bool _interactiveBeforeAnnotation = true;
    private bool _isStatusBadgeHovered;
    private HwndSource? _hwndSource;
    private BitmapSource? _latestCapture;
    private AnnotationTool _activeTool = AnnotationTool.Pen;
    private readonly List<System.Windows.Shapes.Rectangle> _drawnRectangles = [];
    private readonly List<TextAnnotationVisual> _textAnnotations = [];
    private readonly Dictionary<FrameworkElement, TextAnnotationVisual> _textAnnotationLookup = [];
    private readonly List<AnnotationAction> _annotationHistory = [];
    private readonly List<AnnotationAction> _redoHistory = [];
    private System.Windows.Shapes.Rectangle? _previewRectangle;
    private System.Windows.Point? _rectangleStartPoint;
    private System.Windows.Controls.TextBox? _activeTextEditor;
    private TextAnnotationVisual? _selectedTextAnnotation;
    private System.Windows.Media.Color _annotationColor = System.Windows.Media.Colors.Red;
    private int _annotationSessionVersion;
    private bool _isDraggingStatusBadge;
    private System.Windows.Point _statusBadgeDragStartScreen;
    private double _statusBadgeDragStartLeft;
    private double _statusBadgeDragStartTop;
    private Int32Rect _frameBoundsBeforeAnnotation;
    private Int32Rect _windowBoundsBeforeAnnotation;
    private double _leftBeforeAnnotation;
    private double _topBeforeAnnotation;
    private double _widthBeforeAnnotation;
    private double _heightBeforeAnnotation;

    public OverlayWindow(
        CaptureService captureService,
        ClipboardService clipboardService,
        HotkeyService hotkeyService,
        SettingsService settingsService,
        AppSettings settings,
        string frameLabel,
        bool initiallyOwnsGlobalHotkeys)
    {
        InitializeComponent();

        _captureService = captureService;
        _clipboardService = clipboardService;
        _hotkeyService = hotkeyService;
        _settingsService = settingsService;
        _settings = settings;
        _initiallyOwnsGlobalHotkeys = initiallyOwnsGlobalHotkeys;
        Title = frameLabel;
        _hoverTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _hoverTimer.Tick += HoverTimer_OnTick;

        ApplySettings();
        ConfigureAnnotationCanvas();
        UpdateModeUi();
        UpdateSizeLabel();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += OnActivated;
        LocationChanged += (_, _) => UpdateSizeLabel();
        SizeChanged += (_, _) =>
        {
            UpdateSizeLabel();
            UpdateStatusBadgePresentation(animate: false);
        };
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonUp += (_, _) => HideSnapGuides();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureVisibleOnScreen();
        ToggleInteractivity(true);
        _hoverTimer.Start();
        Activate();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);

        if (_initiallyOwnsGlobalHotkeys)
        {
            _hotkeyService.Register(_hwndSource.Handle);
        }

        ApplyClickThrough();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.SetHotkeyOwner(this);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _hoverTimer.Stop();
        SaveSettings();
        _hotkeyService.Unregister();

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_isAnnotating && e.Key == Key.Escape)
        {
            ExitAnnotationMode();
            e.Handled = true;
            return;
        }

        if (_isAnnotating && e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            UndoLastAnnotation();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (System.Windows.Application.Current is App app)
            {
                app.CreateNewFrameFromHotkey();
            }

            e.Handled = true;
            return;
        }

        if (_isAnnotating && e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            RedoLastAnnotation();
            e.Handled = true;
            return;
        }

        if (_isAnnotating && e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopyAnnotatedImageToClipboard(returnToInactive: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isInteractive)
        {
            ToggleInteractivity(false);
            e.Handled = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmNcHitTest && !_isAnnotating)
        {
            if (IsPointOverCaptureButton(lParam) || IsPointOverElement(StatusBadge, lParam))
            {
                return IntPtr.Zero;
            }

            if (_isInteractive && IsPointInResizeZone(lParam))
            {
                return IntPtr.Zero;
            }

            handled = true;
            return new IntPtr(NativeMethods.HtTransparent);
        }

        if (msg == NativeMethods.WmHotKey)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyService.ToggleModeHotkeyId:
                    ToggleInteractivity(!_isInteractive);
                    handled = true;
                    break;
                case HotkeyService.CaptureHotkeyId:
                    if (_isAnnotating)
                    {
                        CopyAnnotatedImageToClipboard();
                    }
                    else
                    {
                        StartAnnotationCapture();
                    }
                    handled = true;
                    break;
                case HotkeyService.ExitHotkeyId:
                    if (_isAnnotating)
                    {
                        ExitAnnotationMode();
                    }
                    else
                    {
                        Close();
                    }
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ToggleInteractivity(bool interactive)
    {
        _isInteractive = interactive;
        if (!_isInteractive)
        {
            _isStatusBadgeHovered = false;
        }

        ApplyClickThrough();
        UpdateModeUi();

        if (_isInteractive)
        {
            Activate();
            Focus();
        }
    }

    private void ApplyClickThrough()
    {
        if (_hwndSource is null)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetWindowLong(_hwndSource.Handle, NativeMethods.GwlExStyle);
        extendedStyle |= NativeMethods.WsExLayered;

        NativeMethods.SetWindowLong(_hwndSource.Handle, NativeMethods.GwlExStyle, extendedStyle);
    }

    private void UpdateModeUi()
    {
        FrameModeLayer.Visibility = _isAnnotating ? Visibility.Collapsed : Visibility.Visible;
        AnnotationLayer.Visibility = _isAnnotating ? Visibility.Visible : Visibility.Collapsed;

        if (_isAnnotating)
        {
            return;
        }

        ModeLabel.Text = _isInteractive ? "Active" : "Inactive";
        HintLabel.Text = _isInteractive
            ? "Drag anywhere inside, Ctrl+Shift+N new frame, Ctrl+Shift+S capture, Ctrl+Shift+Q quit"
            : "Ctrl+Shift+Space edit, Ctrl+Shift+N new frame, Ctrl+Shift+Q quit";

        FrameBorder.BorderBrush = _isInteractive
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245))
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(78, 245, 245, 245));

        FrameBorder.Background = System.Windows.Media.Brushes.Transparent;
        StatusBadge.Background = _isInteractive
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(232, 20, 20, 20))
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 20, 20, 20));
        StatusBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(_isInteractive ? (byte)51 : (byte)36, 255, 255, 255));
        StatusBadge.Cursor = _isInteractive ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Arrow;
        SizeBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(232, 20, 20, 20));
        SizeBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(36, 255, 255, 255));
        StatusBadge.Visibility = _isInteractive ? Visibility.Visible : Visibility.Collapsed;
        SizeBadge.Visibility = _isInteractive ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusBadgePresentation(animate: false);

        var thumbVisibility = _isInteractive ? Visibility.Visible : Visibility.Collapsed;

        MoveThumb.Visibility = Visibility.Collapsed;
        LeftThumb.Visibility = thumbVisibility;
        RightThumb.Visibility = thumbVisibility;
        TopThumb.Visibility = thumbVisibility;
        BottomThumb.Visibility = thumbVisibility;
        TopLeftThumb.Visibility = thumbVisibility;
        TopRightThumb.Visibility = thumbVisibility;
        BottomLeftThumb.Visibility = thumbVisibility;
        BottomRightThumb.Visibility = thumbVisibility;
        CaptureButton.Visibility = _isInteractive ? Visibility.Visible : Visibility.Collapsed;
        SelectionCaptureButton.Visibility = _isInteractive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HoverTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isAnnotating)
        {
            StatusBadge.Visibility = Visibility.Collapsed;
            CaptureButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        if (_isInteractive)
        {
            StatusBadge.Visibility = Visibility.Visible;
            CaptureButton.Visibility = Visibility.Visible;
            SelectionCaptureButton.Visibility = Visibility.Visible;
            return;
        }

        var isInsideBounds = IsCursorInsideFrameBounds();
        if (isInsideBounds && System.Windows.Application.Current is App app)
        {
            app.SetHotkeyOwner(this);
        }

        StatusBadge.Visibility = isInsideBounds ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Visibility = isInsideBounds ? Visibility.Visible : Visibility.Collapsed;
        SelectionCaptureButton.Visibility = isInsideBounds ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSizeLabel()
    {
        var rect = GetScreenBounds();
        SizeLabel.Text = $"{rect.Width} x {rect.Height}";
    }

    private Int32Rect GetScreenBounds()
    {
        if (FrameBorder.IsLoaded && FrameBorder.ActualWidth > 0 && FrameBorder.ActualHeight > 0)
        {
            var topLeft = FrameBorder.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = FrameBorder.PointToScreen(new System.Windows.Point(FrameBorder.ActualWidth, FrameBorder.ActualHeight));

            return new Int32Rect(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X)),
                Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y)));
        }

        var (paddingX, paddingY) = GetFramePaddingInScreenPixels();

        if (_hwndSource is null)
        {
            return new Int32Rect(
                (int)Math.Round(Left + paddingX),
                (int)Math.Round(Top + paddingY),
                (int)Math.Round(Math.Max(1, Width - (paddingX * 2))),
                (int)Math.Round(Math.Max(1, Height - (paddingY * 2))));
        }

        var rect = NativeMethods.GetWindowRectangle(_hwndSource.Handle);

        return new Int32Rect(
            rect.X + (int)Math.Round(paddingX),
            rect.Y + (int)Math.Round(paddingY),
            Math.Max(1, rect.Width - (int)Math.Round(paddingX * 2)),
            Math.Max(1, rect.Height - (int)Math.Round(paddingY * 2)));
    }

    private void StartAnnotationCapture()
    {
        var rect = GetScreenBounds();
        StartAnnotationCaptureForBounds(rect);
    }

    private void StartAnnotationCaptureForBounds(Int32Rect rect)
    {
        using var hiddenState = new TemporaryWindowHideScope(this);
        var image = _captureService.Capture(rect);
        _clipboardService.Copy(image);
        EnterAnnotationMode(image, rect, preserveCurrentFrameBounds: true);
    }

    private void EnterAnnotationMode(BitmapSource image, Int32Rect capturedBounds, bool preserveCurrentFrameBounds = false)
    {
        _annotationSessionVersion++;
        _latestCapture = image;
        _interactiveBeforeAnnotation = _isInteractive;
        _frameBoundsBeforeAnnotation = GetScreenBounds();
        _windowBoundsBeforeAnnotation = _hwndSource is not null
            ? NativeMethods.GetWindowRectangle(_hwndSource.Handle)
            : new Int32Rect(
                (int)Math.Round(Left),
                (int)Math.Round(Top),
                Math.Max(1, (int)Math.Round(Width)),
                Math.Max(1, (int)Math.Round(Height)));
        _leftBeforeAnnotation = Left;
        _topBeforeAnnotation = Top;
        _widthBeforeAnnotation = Width;
        _heightBeforeAnnotation = Height;
        _isAnnotating = true;
        _isConventionalAnnotationSession = !preserveCurrentFrameBounds;
        _isInteractive = true;

        if (preserveCurrentFrameBounds)
        {
            AnnotationContentHost.Width = FrameBorder.ActualWidth;
            AnnotationContentHost.Height = FrameBorder.ActualHeight;
        }
        else
        {
            var sampleX = capturedBounds.X + Math.Max(1, capturedBounds.Width / 2);
            var sampleY = capturedBounds.Y + Math.Max(1, capturedBounds.Height / 2);
            var (scaleX, scaleY) = NativeMethods.GetScaleForScreenPoint(sampleX, sampleY);
            SetFrameBounds(capturedBounds, enforceMinimumSize: false);
            AnnotationContentHost.Width = Math.Ceiling(capturedBounds.Width / scaleX);
            AnnotationContentHost.Height = Math.Ceiling(capturedBounds.Height / scaleY);
        }

        Width = Math.Max(Width, AnnotationToolbarWidth);
        Top -= AnnotationToolbarHeight;
        Height += AnnotationToolbarHeight;
        CapturedImage.Source = image;
        AnnotationCanvas.Strokes.Clear();
        ShapeCanvas.Children.Clear();
        _drawnRectangles.Clear();
        _textAnnotations.Clear();
        _textAnnotationLookup.Clear();
        _annotationHistory.Clear();
        _redoHistory.Clear();
        _previewRectangle = null;
        _rectangleStartPoint = null;
        _selectedTextAnnotation = null;
        CancelActiveTextEditor();
        SetAnnotationTool(AnnotationTool.Pen);
        UpdateModeUi();
        ApplyClickThrough();
        HintLabel.Text = $"Original copied {image.PixelWidth} x {image.PixelHeight}. Draw, then Copy or Ctrl+C.";
        Activate();
        Focus();

        if (!preserveCurrentFrameBounds)
        {
            ScheduleAnnotatedCaptureAlignment(capturedBounds, _annotationSessionVersion);
        }
    }

    private void ExitAnnotationMode()
    {
        _annotationSessionVersion++;
        var shouldSuppressDuringRestore = _isConventionalAnnotationSession;
        if (shouldSuppressDuringRestore)
        {
            Opacity = 0;
        }

        _isAnnotating = false;
        _isInteractive = _interactiveBeforeAnnotation;
        if (_hwndSource is not null && _windowBoundsBeforeAnnotation.Width > 0 && _windowBoundsBeforeAnnotation.Height > 0)
        {
            NativeMethods.SetWindowPos(
                _hwndSource.Handle,
                IntPtr.Zero,
                _windowBoundsBeforeAnnotation.X,
                _windowBoundsBeforeAnnotation.Y,
                _windowBoundsBeforeAnnotation.Width,
                _windowBoundsBeforeAnnotation.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);

            // Keep WPF's logical window metrics in sync with the native restore so
            // repeated conventional captures on mixed-DPI monitors do not accumulate
            // size drift between sessions.
            Left = _leftBeforeAnnotation;
            Top = _topBeforeAnnotation;
            Width = _widthBeforeAnnotation;
            Height = _heightBeforeAnnotation;
        }
        else if (_frameBoundsBeforeAnnotation.Width > 0 && _frameBoundsBeforeAnnotation.Height > 0)
        {
            SetFrameBounds(_frameBoundsBeforeAnnotation, enforceMinimumSize: false);
        }
        else
        {
            Left = _leftBeforeAnnotation;
            Top = _topBeforeAnnotation;
            Width = _widthBeforeAnnotation;
            Height = _heightBeforeAnnotation;
        }

        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Opacity = 1;
        AnnotationContentHost.Width = double.NaN;
        AnnotationContentHost.Height = double.NaN;
        CapturedImage.Source = null;
        AnnotationCanvas.Strokes.Clear();
        ShapeCanvas.Children.Clear();
        _drawnRectangles.Clear();
        _textAnnotations.Clear();
        _textAnnotationLookup.Clear();
        _annotationHistory.Clear();
        _redoHistory.Clear();
        _previewRectangle = null;
        _rectangleStartPoint = null;
        _selectedTextAnnotation = null;
        CancelActiveTextEditor();
        _isConventionalAnnotationSession = false;
        UpdateModeUi();
        ApplyClickThrough();
        Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));

        if (shouldSuppressDuringRestore)
        {
            Opacity = 1;
        }
    }

    private void ConfigureAnnotationCanvas()
    {
        AnnotationCanvas.DefaultDrawingAttributes = CreateDrawingAttributes(_annotationColor, _activeTool);
        AnnotationCanvas.EditingMode = System.Windows.Controls.InkCanvasEditingMode.Ink;
        AnnotationCanvas.PreviewMouseLeftButtonDown += AnnotationCanvas_OnPreviewMouseLeftButtonDown;
        AnnotationCanvas.PreviewMouseMove += AnnotationCanvas_OnPreviewMouseMove;
        AnnotationCanvas.StrokeCollected += AnnotationCanvas_OnStrokeCollected;
        ShapeCanvas.MouseLeftButtonDown += ShapeCanvas_OnMouseLeftButtonDown;
        ShapeCanvas.MouseMove += ShapeCanvas_OnMouseMove;
        ShapeCanvas.MouseLeftButtonUp += ShapeCanvas_OnMouseLeftButtonUp;
        SetAnnotationTool(AnnotationTool.Pen);
        UpdateColorButtons();
    }

    private void CopyAnnotatedImageToClipboard(bool returnToInactive = false)
    {
        var bitmap = RenderAnnotatedBitmap();
        if (bitmap is null)
        {
            return;
        }

        _clipboardService.Copy(bitmap);
        ExitAnnotationMode();

        if (returnToInactive)
        {
            ToggleInteractivity(false);
        }

        if (!_isInteractive)
        {
            HintLabel.Text = "Ctrl+Shift+Space edit, Ctrl+Shift+N new frame, Ctrl+Shift+Q quit";
        }
        else
        {
            HintLabel.Text = $"Copied {bitmap.PixelWidth} x {bitmap.PixelHeight} to clipboard";
        }
    }

    private BitmapSource? RenderAnnotatedBitmap()
    {
        var renderWidth = CapturedImage.ActualWidth;
        var renderHeight = CapturedImage.ActualHeight;

        if (_latestCapture is null || renderWidth <= 0 || renderHeight <= 0)
        {
            return null;
        }

        var surface = new Grid
        {
            Width = renderWidth,
            Height = renderHeight,
            Background = System.Windows.Media.Brushes.Transparent
        };

        surface.Children.Add(new System.Windows.Controls.Image
        {
            Source = _latestCapture,
            Stretch = Stretch.Fill
        });

        surface.Children.Add(new System.Windows.Controls.InkPresenter
        {
            Strokes = AnnotationCanvas.Strokes.Clone()
        });

        var rectangleCanvas = new Canvas();
        foreach (var rectangle in _drawnRectangles)
        {
            rectangleCanvas.Children.Add(CloneRectangle(rectangle));
        }
        surface.Children.Add(rectangleCanvas);

        var textCanvas = new Canvas();
        foreach (var textAnnotation in _textAnnotations)
        {
            textCanvas.Children.Add(CloneTextAnnotation(textAnnotation));
        }
        surface.Children.Add(textCanvas);

        surface.Measure(new System.Windows.Size(renderWidth, renderHeight));
        surface.Arrange(new Rect(0, 0, renderWidth, renderHeight));
        surface.UpdateLayout();

        var dpiX = 96d * _latestCapture.PixelWidth / renderWidth;
        var dpiY = 96d * _latestCapture.PixelHeight / renderHeight;
        var rtb = new RenderTargetBitmap(
            _latestCapture.PixelWidth,
            _latestCapture.PixelHeight,
            dpiX,
            dpiY,
            PixelFormats.Pbgra32);

        rtb.Render(surface);
        rtb.Freeze();
        return rtb;
    }

    private void ApplySettings()
    {
        var horizontalPaddingDip = FrameInteractionPadding;
        var verticalPaddingDip = FrameInteractionPadding;

        Left = _settings.Left - horizontalPaddingDip;
        Top = _settings.Top - verticalPaddingDip;
        Width = Math.Max(MinimumFrameWidth + (horizontalPaddingDip * 2), _settings.Width + (horizontalPaddingDip * 2));
        Height = Math.Max(MinimumFrameHeight + (verticalPaddingDip * 2), _settings.Height + (verticalPaddingDip * 2));
    }

    private void EnsureVisibleOnScreen()
    {
        if (_hwndSource is null)
        {
            return;
        }

        var area = System.Windows.Forms.Screen.FromHandle(_hwndSource.Handle).WorkingArea;
        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        var left = Left;
        var top = Top;

        if (left < area.Left || left > area.Right - 80)
        {
            left = area.Left + Math.Max(24, (area.Width - width) / 2d);
        }

        if (top < area.Top || top > area.Bottom - 80)
        {
            top = area.Top + Math.Max(24, (area.Height - height) / 2d);
        }

        left = Math.Max(area.Left, Math.Min(left, area.Right - width));
        top = Math.Max(area.Top, Math.Min(top, area.Bottom - height));

        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    private void SaveSettings()
    {
        var bounds = GetScreenBounds();
        _settings.Left = bounds.X;
        _settings.Top = bounds.Y;
        _settings.Width = bounds.Width;
        _settings.Height = bounds.Height;
        _settings.StartInteractive = _isInteractive;

        _settingsService.Save(_settings);
    }

    public AppSettings GetFrameSettingsSnapshot()
    {
        var bounds = GetScreenBounds();
        return new AppSettings
        {
            Left = bounds.X,
            Top = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            StartInteractive = _isInteractive
        };
    }

    private void MoveThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = Left + e.HorizontalChange;
        var top = Top + e.VerticalChange;
        var width = Width;
        var height = Height;

        ApplySnapGuides(ref left, ref top, ref width, ref height, snapLeft: true, snapTop: true, snapRight: true, snapBottom: true);

        Left = left;
        Top = top;
    }

    private void CaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        StartAnnotationCapture();
    }

    private void SelectionCaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        StartSelectionCapture();
    }

    public void ShowOverlay()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    public void TriggerCapture()
    {
        StartAnnotationCapture();
    }

    public void TriggerSelectionCapture()
    {
        StartSelectionCapture();
    }

    public void SetGlobalHotkeysEnabled(bool enabled)
    {
        if (_hwndSource is null)
        {
            return;
        }

        if (enabled)
        {
            _hotkeyService.Register(_hwndSource.Handle);
        }
        else
        {
            _hotkeyService.Unregister();
        }
    }

    private void UndoAnnotationButton_OnClick(object sender, RoutedEventArgs e)
    {
        UndoLastAnnotation();
    }

    private void RedoAnnotationButton_OnClick(object sender, RoutedEventArgs e)
    {
        RedoLastAnnotation();
    }

    private void UndoLastAnnotation()
    {
        if (_annotationHistory.Count == 0)
        {
            return;
        }

        var action = _annotationHistory[^1];
        _annotationHistory.RemoveAt(_annotationHistory.Count - 1);
        _redoHistory.Add(action);

        if (action.Kind == AnnotationActionKind.Element && action.Element is not null)
        {
            RemoveAnnotationElement(action.Element);
            return;
        }

        if (action.Kind == AnnotationActionKind.Stroke && action.Stroke is not null)
        {
            AnnotationCanvas.Strokes.Remove(action.Stroke);
        }
    }

    private void RedoLastAnnotation()
    {
        if (_redoHistory.Count == 0)
        {
            return;
        }

        var action = _redoHistory[^1];
        _redoHistory.RemoveAt(_redoHistory.Count - 1);

        if (action.Kind == AnnotationActionKind.Element && action.Element is not null)
        {
            if (action.Element is System.Windows.Shapes.Rectangle rectangle)
            {
                if (!_drawnRectangles.Contains(rectangle))
                {
                    _drawnRectangles.Add(rectangle);
                }
            }
            else if (_textAnnotationLookup.TryGetValue(action.Element, out var textAnnotation))
            {
                if (!_textAnnotations.Contains(textAnnotation))
                {
                    _textAnnotations.Add(textAnnotation);
                }
            }

            if (!ShapeCanvas.Children.Contains(action.Element))
            {
                ShapeCanvas.Children.Add(action.Element);
            }
        }
        else if (action.Kind == AnnotationActionKind.Stroke && action.Stroke is not null)
        {
            if (!AnnotationCanvas.Strokes.Contains(action.Stroke))
            {
                AnnotationCanvas.Strokes.Add(action.Stroke);
            }
        }

        _annotationHistory.Add(action);
    }

    private void ClearAnnotationButton_OnClick(object sender, RoutedEventArgs e)
    {
        AnnotationCanvas.Strokes.Clear();
        ShapeCanvas.Children.Clear();
        _drawnRectangles.Clear();
        _textAnnotations.Clear();
        _textAnnotationLookup.Clear();
        _annotationHistory.Clear();
        _redoHistory.Clear();
        _previewRectangle = null;
        _rectangleStartPoint = null;
        _selectedTextAnnotation = null;
        CancelActiveTextEditor();
    }

    private void CopyAnnotationButton_OnClick(object sender, RoutedEventArgs e)
    {
        CopyAnnotatedImageToClipboard();
    }

    private void SaveAnnotationButton_OnClick(object sender, RoutedEventArgs e)
    {
        SaveAnnotatedImageToFile();
    }

    private async void OcrAnnotationButton_OnClick(object sender, RoutedEventArgs e)
    {
        await CopyOcrTextToClipboardAsync();
    }

    private void CloseAnnotationButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExitAnnotationMode();
    }

    private void SaveAnnotatedImageToFile()
    {
        var bitmap = RenderAnnotatedBitmap();
        if (bitmap is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG Image|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        using var stream = File.Create(dialog.FileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private async Task CopyOcrTextToClipboardAsync()
    {
        var source = _latestCapture;
        if (source is null)
        {
            return;
        }

        var text = await RecognizeTextAsync(source);
        if (!string.IsNullOrWhiteSpace(text))
        {
            System.Windows.Clipboard.SetText(text);
        }
    }

    private static async Task<string> RecognizeTextAsync(BitmapSource bitmap)
    {
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(stream.ToArray().AsBuffer());
        ras.Seek(0);

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();

        if (engine is null)
        {
            return string.Empty;
        }

        var result = await engine.RecognizeAsync(softwareBitmap);
        return result.Text;
    }

    private void PenToolButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(AnnotationTool.Pen);
    }

    private void HighlighterToolButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(AnnotationTool.Highlighter);
    }

    private void TextToolButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(AnnotationTool.Text);
    }

    private void EraserToolButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(AnnotationTool.Eraser);
    }

    private void RectangleToolButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(AnnotationTool.Rectangle);
    }

    private void RedactToolButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(AnnotationTool.Redact);
    }

    private bool IsPointOverCaptureButton(IntPtr lParam)
    {
        return IsPointOverElement(CaptureButton, lParam) ||
               IsPointOverElement(SelectionCaptureButton, lParam);
    }

    private bool IsPointInResizeZone(IntPtr lParam)
    {
        var point = GetScreenPoint(lParam);
        var bounds = GetScreenBounds();

        var onLeftOrRightEdge =
            point.Y >= bounds.Y - ResizeActivationMargin &&
            point.Y <= bounds.Y + bounds.Height + ResizeActivationMargin &&
            (Math.Abs(point.X - bounds.X) <= ResizeActivationMargin ||
             Math.Abs(point.X - (bounds.X + bounds.Width)) <= ResizeActivationMargin);

        var onTopOrBottomEdge =
            point.X >= bounds.X - ResizeActivationMargin &&
            point.X <= bounds.X + bounds.Width + ResizeActivationMargin &&
            (Math.Abs(point.Y - bounds.Y) <= ResizeActivationMargin ||
             Math.Abs(point.Y - (bounds.Y + bounds.Height)) <= ResizeActivationMargin);

        return onLeftOrRightEdge || onTopOrBottomEdge;
    }

    private bool IsCursorInsideFrameBounds()
    {
        var cursor = NativeMethods.GetCursorScreenPosition();
        var bounds = GetScreenBounds();
        return cursor.X >= bounds.X &&
               cursor.X <= bounds.X + bounds.Width &&
               cursor.Y >= bounds.Y &&
               cursor.Y <= bounds.Y + bounds.Height;
    }

    private static bool IsPointOverElement(FrameworkElement element, IntPtr lParam)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var point = GetScreenPoint(lParam);
        var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));

        return point.X >= topLeft.X && point.X <= bottomRight.X && point.Y >= topLeft.Y && point.Y <= bottomRight.Y;
    }

    private static System.Windows.Point GetScreenPoint(IntPtr lParam)
    {
        const int mask = 0xFFFF;
        var x = (short)(lParam.ToInt32() & mask);
        var y = (short)((lParam.ToInt32() >> 16) & mask);
        return new System.Windows.Point(x, y);
    }

    private void StatusBadge_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isStatusBadgeHovered = true;
        if (System.Windows.Application.Current is App app)
        {
            app.SetHotkeyOwner(this);
        }

        UpdateStatusBadgePresentation(animate: true);
    }

    private void StatusBadge_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isStatusBadgeHovered = false;
        UpdateStatusBadgePresentation(animate: true);
    }

    private void StatusBadge_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isInteractive || _isAnnotating || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _isDraggingStatusBadge = true;
        _statusBadgeDragStartScreen = PointToScreen(e.GetPosition(this));
        _statusBadgeDragStartLeft = Left;
        _statusBadgeDragStartTop = Top;
        StatusBadge.CaptureMouse();
        e.Handled = true;
    }

    private void StatusBadge_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingStatusBadge || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentScreen = PointToScreen(e.GetPosition(this));
        var left = _statusBadgeDragStartLeft + (currentScreen.X - _statusBadgeDragStartScreen.X);
        var top = _statusBadgeDragStartTop + (currentScreen.Y - _statusBadgeDragStartScreen.Y);
        var width = Width;
        var height = Height;

        ApplySnapGuides(ref left, ref top, ref width, ref height, snapLeft: true, snapTop: true, snapRight: true, snapBottom: true);

        Left = left;
        Top = top;
    }

    private void StatusBadge_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndStatusBadgeDrag();
    }

    private void StatusBadge_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        EndStatusBadgeDrag();
    }

    private void EndStatusBadgeDrag()
    {
        _isDraggingStatusBadge = false;
        if (StatusBadge.IsMouseCaptured)
        {
            StatusBadge.ReleaseMouseCapture();
        }
    }

    private void UpdateStatusBadgePresentation(bool animate)
    {
        if (_isAnnotating)
        {
            return;
        }

        var shouldExpand = _isInteractive || _isStatusBadgeHovered;
        var expandedWidth = _isInteractive ? ExpandedActiveStatusBadgeWidth : ExpandedInactiveStatusBadgeWidth;
        var requestedWidth = shouldExpand ? expandedWidth : CompactStatusBadgeWidth;
        var availableFrameWidth = FrameBorder.ActualWidth > 0
            ? FrameBorder.ActualWidth - StatusBadgeLeftInsetWithinFrame - StatusBadgeRightSafetyGap - StatusBadgeReservedRightZone
            : ActualWidth - StatusBadgeHorizontalMargin - StatusBadgeRightSafetyGap;
        var maxVisibleWidth = Math.Max(
            CompactStatusBadgeWidth,
            availableFrameWidth);
        var targetWidth = Math.Min(requestedWidth, maxVisibleWidth);
        var targetOpacity = shouldExpand ? 1d : 0d;
        var isClipped = requestedWidth > maxVisibleWidth;

        if (!_isInteractive)
        {
            HintLabel.Text = "Ctrl+Shift+Space edit, Ctrl+Shift+N new frame, Ctrl+Shift+Q quit";
        }

        StatusBadge.OpacityMask = isClipped
            ? CreateStatusBadgeFadeMask()
            : null;

        if (!animate)
        {
            StatusBadge.BeginAnimation(WidthProperty, null);
            HintLabel.BeginAnimation(OpacityProperty, null);
            StatusBadge.Width = targetWidth;
            HintLabel.Visibility = shouldExpand ? Visibility.Visible : Visibility.Collapsed;
            HintLabel.Opacity = targetOpacity;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(180);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        if (shouldExpand)
        {
            HintLabel.Visibility = Visibility.Visible;
        }

        StatusBadge.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, duration)
        {
            EasingFunction = easing
        });

        var opacityAnimation = new DoubleAnimation(targetOpacity, duration)
        {
            EasingFunction = easing
        };

        if (!shouldExpand)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (!_isInteractive && !_isStatusBadgeHovered)
                {
                    HintLabel.Visibility = Visibility.Collapsed;
                }
            };
        }

        HintLabel.BeginAnimation(OpacityProperty, opacityAnimation);
    }

    private static System.Windows.Media.Brush CreateStatusBadgeFadeMask()
    {
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(System.Windows.Media.Colors.White, 0),
                new GradientStop(System.Windows.Media.Colors.White, 0.92),
                new GradientStop(System.Windows.Media.Color.FromArgb(89, 255, 255, 255), 0.975),
                new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 1),
            },
            new System.Windows.Point(0, 0),
            new System.Windows.Point(1, 0));
    }

    private void StartSelectionCapture()
    {
        using var hiddenState = new TemporaryWindowHideScope(this);
        var selectionWindow = new SelectionOverlayWindow();

        var confirmed = selectionWindow.ShowDialog();
        if (confirmed != true || selectionWindow.SelectedBounds is not Int32Rect selectedBounds)
        {
            return;
        }

        var image = _captureService.Capture(selectedBounds);
        _clipboardService.Copy(image);
        EnterAnnotationMode(image, selectedBounds);
    }

    private void SetFrameBounds(Int32Rect bounds, bool enforceMinimumSize = true)
    {
        var sampleX = bounds.X + Math.Max(1, bounds.Width / 2);
        var sampleY = bounds.Y + Math.Max(1, bounds.Height / 2);
        var (scaleX, scaleY) = NativeMethods.GetScaleForScreenPoint(sampleX, sampleY);
        var horizontalPaddingDip = FrameInteractionPadding / scaleX;
        var verticalPaddingDip = FrameInteractionPadding / scaleY;

        Left = (bounds.X / scaleX) - horizontalPaddingDip;
        Top = (bounds.Y / scaleY) - verticalPaddingDip;
        var targetWidth = Math.Ceiling(bounds.Width / scaleX) + (horizontalPaddingDip * 2);
        var targetHeight = Math.Ceiling(bounds.Height / scaleY) + (verticalPaddingDip * 2);

        if (enforceMinimumSize)
        {
            targetWidth = Math.Max(MinimumFrameWidth + (horizontalPaddingDip * 2), targetWidth);
            targetHeight = Math.Max(MinimumFrameHeight + (verticalPaddingDip * 2), targetHeight);
        }

        Width = targetWidth;
        Height = targetHeight;
    }

    private void ScheduleAnnotatedCaptureAlignment(Int32Rect targetBounds, int annotationSessionVersion)
    {
        Dispatcher.InvokeAsync(() =>
        {
            AlignAnnotatedCaptureToBounds(targetBounds, annotationSessionVersion);
            Dispatcher.InvokeAsync(() => AlignAnnotatedCaptureToBounds(targetBounds, annotationSessionVersion), DispatcherPriority.ApplicationIdle);
        }, DispatcherPriority.Render);
    }

    private void AlignAnnotatedCaptureToBounds(Int32Rect targetBounds, int annotationSessionVersion)
    {
        if (_hwndSource is null ||
            !_isAnnotating ||
            !_isConventionalAnnotationSession ||
            annotationSessionVersion != _annotationSessionVersion ||
            AnnotationContentHost.ActualWidth <= 0 ||
            AnnotationContentHost.ActualHeight <= 0)
        {
            return;
        }

        var currentTopLeft = AnnotationContentHost.PointToScreen(new System.Windows.Point(0, 0));
        var currentBottomRight = AnnotationContentHost.PointToScreen(new System.Windows.Point(AnnotationContentHost.ActualWidth, AnnotationContentHost.ActualHeight));
        var currentWindowRect = NativeMethods.GetWindowRectangle(_hwndSource.Handle);

        var deltaX = (int)Math.Round(targetBounds.X - currentTopLeft.X);
        var deltaY = (int)Math.Round(targetBounds.Y - currentTopLeft.Y);
        var widthDelta = (int)Math.Round(targetBounds.Width - (currentBottomRight.X - currentTopLeft.X));
        var heightDelta = (int)Math.Round(targetBounds.Height - (currentBottomRight.Y - currentTopLeft.Y));

        if (Math.Abs(deltaX) <= 1 && Math.Abs(deltaY) <= 1 && Math.Abs(widthDelta) <= 1 && Math.Abs(heightDelta) <= 1)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwndSource.Handle,
            IntPtr.Zero,
            currentWindowRect.X + deltaX,
            currentWindowRect.Y + deltaY,
            currentWindowRect.Width + widthDelta,
            currentWindowRect.Height + heightDelta,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private (double X, double Y) GetFramePaddingInScreenPixels()
    {
        return (FrameInteractionPadding * GetHorizontalScreenScale(), FrameInteractionPadding * GetVerticalScreenScale());
    }

    private double GetHorizontalScreenScale()
    {
        return _hwndSource?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
    }

    private double GetVerticalScreenScale()
    {
        return _hwndSource?.CompositionTarget?.TransformToDevice.M22 ?? 1d;
    }

    private sealed class TemporaryWindowHideScope : IDisposable
    {
        private readonly OverlayWindow _window;
        private readonly double _previousOpacity;
        private readonly bool _previousTopmost;

        public TemporaryWindowHideScope(OverlayWindow window)
        {
            _window = window;
            _previousOpacity = window.Opacity;
            _previousTopmost = window.Topmost;

            window.Topmost = false;
            window.Opacity = 0;
            window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            Thread.Sleep(75);
        }

        public void Dispose()
        {
            _window.Opacity = _previousOpacity;
            _window.Topmost = _previousTopmost;
            _window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        }
    }


    private void SetAnnotationTool(AnnotationTool tool)
    {
        CommitActiveTextEditor();
        if (tool != AnnotationTool.Text)
        {
            SelectTextAnnotation(null);
        }

        _activeTool = tool;

        var activeBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(216, 58, 52));
        var inactiveBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(232, 20, 20, 20));
        var activeBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(115, 255, 255, 255));
        var inactiveBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 255, 255, 255));

        PenToolButton.Background = tool == AnnotationTool.Pen ? activeBrush : inactiveBrush;
        HighlighterToolButton.Background = tool == AnnotationTool.Highlighter ? activeBrush : inactiveBrush;
        TextToolButton.Background = tool == AnnotationTool.Text ? activeBrush : inactiveBrush;
        EraserToolButton.Background = tool == AnnotationTool.Eraser ? activeBrush : inactiveBrush;
        RectangleToolButton.Background = tool == AnnotationTool.Rectangle ? activeBrush : inactiveBrush;
        RedactToolButton.Background = tool == AnnotationTool.Redact ? activeBrush : inactiveBrush;
        PenToolButton.BorderBrush = tool == AnnotationTool.Pen ? activeBorderBrush : inactiveBorderBrush;
        HighlighterToolButton.BorderBrush = tool == AnnotationTool.Highlighter ? activeBorderBrush : inactiveBorderBrush;
        TextToolButton.BorderBrush = tool == AnnotationTool.Text ? activeBorderBrush : inactiveBorderBrush;
        EraserToolButton.BorderBrush = tool == AnnotationTool.Eraser ? activeBorderBrush : inactiveBorderBrush;
        RectangleToolButton.BorderBrush = tool == AnnotationTool.Rectangle ? activeBorderBrush : inactiveBorderBrush;
        RedactToolButton.BorderBrush = tool == AnnotationTool.Redact ? activeBorderBrush : inactiveBorderBrush;

        AnnotationCanvas.IsHitTestVisible = tool == AnnotationTool.Pen || tool == AnnotationTool.Highlighter || tool == AnnotationTool.Eraser;
        ShapeCanvas.IsHitTestVisible = tool == AnnotationTool.Rectangle || tool == AnnotationTool.Redact || tool == AnnotationTool.Text || tool == AnnotationTool.Eraser;
        ShapeCanvas.Cursor = tool == AnnotationTool.Text ? System.Windows.Input.Cursors.IBeam : System.Windows.Input.Cursors.Arrow;

        AnnotationCanvas.EditingMode = tool switch
        {
            AnnotationTool.Pen => InkCanvasEditingMode.Ink,
            AnnotationTool.Highlighter => InkCanvasEditingMode.Ink,
            AnnotationTool.Eraser => InkCanvasEditingMode.EraseByStroke,
            _ => InkCanvasEditingMode.None,
        };

        UpdateInkDrawingAttributes();
    }

    private void ShapeCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeTool == AnnotationTool.Eraser)
        {
            var position = e.GetPosition(ShapeCanvas);
            EraseAnnotationElementAt(position);
            EraseStrokeAt(position);
            return;
        }

        if (_activeTool != AnnotationTool.Rectangle && _activeTool != AnnotationTool.Redact && _activeTool != AnnotationTool.Text)
        {
            return;
        }

        SelectTextAnnotation(null);
        _rectangleStartPoint = e.GetPosition(ShapeCanvas);
        _previewRectangle = CreateAnnotationRectangle();
        Canvas.SetLeft(_previewRectangle, _rectangleStartPoint.Value.X);
        Canvas.SetTop(_previewRectangle, _rectangleStartPoint.Value.Y);
        ShapeCanvas.Children.Add(_previewRectangle);
        ShapeCanvas.CaptureMouse();
    }

    private void ShapeCanvas_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((_activeTool != AnnotationTool.Rectangle && _activeTool != AnnotationTool.Redact && _activeTool != AnnotationTool.Text) || _previewRectangle is null || _rectangleStartPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(ShapeCanvas);
        UpdateRectangleBounds(_previewRectangle, _rectangleStartPoint.Value, current);
    }

    private void ShapeCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((_activeTool != AnnotationTool.Rectangle && _activeTool != AnnotationTool.Redact && _activeTool != AnnotationTool.Text) || _previewRectangle is null || _rectangleStartPoint is null)
        {
            return;
        }

        var end = e.GetPosition(ShapeCanvas);
        UpdateRectangleBounds(_previewRectangle, _rectangleStartPoint.Value, end);

        if (_previewRectangle.Width >= 4 && _previewRectangle.Height >= 4)
        {
            if (_activeTool == AnnotationTool.Text)
            {
                var textAnnotation = CreateTextAnnotation(_previewRectangle);
                ShapeCanvas.Children.Remove(_previewRectangle);
                ShapeCanvas.Children.Add(textAnnotation.Container);
                _textAnnotations.Add(textAnnotation);
                _textAnnotationLookup[textAnnotation.Container] = textAnnotation;
                _redoHistory.Clear();
                _annotationHistory.Add(new AnnotationAction
                {
                    Kind = AnnotationActionKind.Element,
                    Element = textAnnotation.Container
                });
                SelectTextAnnotation(textAnnotation);
                textAnnotation.Editor.Focus();
                textAnnotation.Editor.SelectAll();
            }
            else
            {
                _drawnRectangles.Add(_previewRectangle);
                _redoHistory.Clear();
                _annotationHistory.Add(new AnnotationAction
                {
                    Kind = AnnotationActionKind.Element,
                    Element = _previewRectangle
                });
            }
        }
        else
        {
            ShapeCanvas.Children.Remove(_previewRectangle);
        }

        _previewRectangle = null;
        _rectangleStartPoint = null;
        ShapeCanvas.ReleaseMouseCapture();
    }

    private System.Windows.Shapes.Rectangle CreateAnnotationRectangle()
    {
        var isRedact = _activeTool == AnnotationTool.Redact;
        var isText = _activeTool == AnnotationTool.Text;
        return new System.Windows.Shapes.Rectangle
        {
            Stroke = isText
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 255, 255))
                : isRedact
                    ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 0, 0, 0))
                    : new SolidColorBrush(_annotationColor),
            StrokeThickness = isText ? 1.5 : isRedact ? 1 : 4,
            RadiusX = 4,
            RadiusY = 4,
            Fill = isRedact ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 0, 0, 0)) : System.Windows.Media.Brushes.Transparent,
            SnapsToDevicePixels = true,
            StrokeDashArray = isText ? [4, 3] : null
        };
    }

    private void AnnotationCanvas_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeTool == AnnotationTool.Eraser)
        {
            var position = e.GetPosition(ShapeCanvas);
            EraseAnnotationElementAt(position);
            EraseStrokeAt(position);
        }
    }

    private void AnnotationCanvas_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_activeTool == AnnotationTool.Eraser && e.LeftButton == MouseButtonState.Pressed)
        {
            var position = e.GetPosition(ShapeCanvas);
            EraseAnnotationElementAt(position);
            EraseStrokeAt(position);
        }
    }

    private void AnnotationCanvas_OnStrokeCollected(object? sender, InkCanvasStrokeCollectedEventArgs e)
    {
        _redoHistory.Clear();
        _annotationHistory.Add(new AnnotationAction
        {
            Kind = AnnotationActionKind.Stroke,
            Stroke = e.Stroke
        });
    }

    private static void UpdateRectangleBounds(System.Windows.Shapes.Rectangle rectangle, System.Windows.Point start, System.Windows.Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);

        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        rectangle.Width = width;
        rectangle.Height = height;
    }

    private static System.Windows.Shapes.Rectangle CloneRectangle(System.Windows.Shapes.Rectangle source)
    {
        var clone = new System.Windows.Shapes.Rectangle
        {
            Width = source.Width,
            Height = source.Height,
            Stroke = source.Stroke,
            StrokeThickness = source.StrokeThickness,
            RadiusX = source.RadiusX,
            RadiusY = source.RadiusY,
            Fill = source.Fill
        };

        Canvas.SetLeft(clone, Canvas.GetLeft(source));
        Canvas.SetTop(clone, Canvas.GetTop(source));
        return clone;
    }

    private FrameworkElement CloneTextAnnotation(TextAnnotationVisual source)
    {
        var border = new Border
        {
            Width = source.Container.Width,
            Height = source.Container.Height,
            Background = System.Windows.Media.Brushes.Transparent,
            Child = new TextBlock
            {
                Text = source.Editor.Text,
                Foreground = System.Windows.Media.Brushes.Black,
                FontFamily = new System.Windows.Media.FontFamily("Arial"),
                FontSize = source.Editor.FontSize,
                TextWrapping = TextWrapping.Wrap
            }
        };

        Canvas.SetLeft(border, Canvas.GetLeft(source.Container));
        Canvas.SetTop(border, Canvas.GetTop(source.Container));
        return border;
    }

    private void RedColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationColor(System.Windows.Media.Color.FromRgb(239, 68, 68));
        ColorPopup.IsOpen = false;
    }

    private void BlueColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationColor(System.Windows.Media.Color.FromRgb(59, 130, 246));
        ColorPopup.IsOpen = false;
    }

    private void GreenColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationColor(System.Windows.Media.Color.FromRgb(34, 197, 94));
        ColorPopup.IsOpen = false;
    }

    private void BlackColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationColor(System.Windows.Media.Color.FromRgb(17, 17, 17));
        ColorPopup.IsOpen = false;
    }

    private void ColorMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = !ColorPopup.IsOpen;
    }

    private void SetAnnotationColor(System.Windows.Media.Color color)
    {
        _annotationColor = color;
        UpdateInkDrawingAttributes();
        UpdateColorButtons();
    }

    private void UpdateInkDrawingAttributes()
    {
        AnnotationCanvas.DefaultDrawingAttributes = CreateDrawingAttributes(_annotationColor, _activeTool);
    }

    private DrawingAttributes CreateDrawingAttributes(System.Windows.Media.Color color, AnnotationTool tool)
    {
        var isHighlighter = tool == AnnotationTool.Highlighter;
        return new DrawingAttributes
        {
            Color = isHighlighter ? System.Windows.Media.Color.FromArgb(120, 255, 235, 59) : color,
            Width = isHighlighter ? 18 : 4,
            Height = isHighlighter ? 18 : 4,
            FitToCurve = true,
            IgnorePressure = true,
            IsHighlighter = isHighlighter,
        };
    }

    private void UpdateColorButtons()
    {
        UpdateColorButtonBorder(RedColorButton, System.Windows.Media.Color.FromRgb(239, 68, 68));
        UpdateColorButtonBorder(BlueColorButton, System.Windows.Media.Color.FromRgb(59, 130, 246));
        UpdateColorButtonBorder(GreenColorButton, System.Windows.Media.Color.FromRgb(34, 197, 94));
        UpdateColorButtonBorder(BlackColorButton, System.Windows.Media.Color.FromRgb(17, 17, 17));
        ActiveColorDot.Fill = new SolidColorBrush(_annotationColor);
    }

    private void UpdateColorButtonBorder(System.Windows.Controls.Button button, System.Windows.Media.Color color)
    {
        button.BorderThickness = _annotationColor == color ? new Thickness(2) : new Thickness(1);
        button.BorderBrush = _annotationColor == color
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255))
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 255, 255, 255));
    }

    private void EraseAnnotationElementAt(System.Windows.Point position)
    {
        for (var index = _drawnRectangles.Count - 1; index >= 0; index--)
        {
            var rectangle = _drawnRectangles[index];
            var left = Canvas.GetLeft(rectangle);
            var top = Canvas.GetTop(rectangle);
            var right = left + rectangle.Width;
            var bottom = top + rectangle.Height;
            var padding = Math.Max(8, rectangle.StrokeThickness * 2);

            var onHorizontalEdge =
                position.X >= left - padding && position.X <= right + padding &&
                (Math.Abs(position.Y - top) <= padding || Math.Abs(position.Y - bottom) <= padding);

            var onVerticalEdge =
                position.Y >= top - padding && position.Y <= bottom + padding &&
                (Math.Abs(position.X - left) <= padding || Math.Abs(position.X - right) <= padding);

            if (!onHorizontalEdge && !onVerticalEdge)
            {
                continue;
            }

            RemoveAnnotationElement(rectangle);
            RemoveElementFromHistory(rectangle);
            break;
        }

        for (var index = _textAnnotations.Count - 1; index >= 0; index--)
        {
            var annotation = _textAnnotations[index];
            var size = GetElementSize(annotation.Container);
            var left = Canvas.GetLeft(annotation.Container);
            var top = Canvas.GetTop(annotation.Container);
            var right = left + size.Width;
            var bottom = top + size.Height;
            var padding = 8d;

            if (position.X < left - padding || position.X > right + padding || position.Y < top - padding || position.Y > bottom + padding)
            {
                continue;
            }

            RemoveAnnotationElement(annotation.Container);
            RemoveElementFromHistory(annotation.Container);
            break;
        }
    }

    private void RemoveElementFromHistory(FrameworkElement element)
    {
        for (var index = _annotationHistory.Count - 1; index >= 0; index--)
        {
            var action = _annotationHistory[index];
            if (action.Kind == AnnotationActionKind.Element && ReferenceEquals(action.Element, element))
            {
                _annotationHistory.RemoveAt(index);
                _redoHistory.Clear();
                return;
            }
        }
    }

    private void RemoveAnnotationElement(FrameworkElement element)
    {
        if (element is System.Windows.Shapes.Rectangle rectangle)
        {
            _drawnRectangles.Remove(rectangle);
        }
        else if (_textAnnotationLookup.TryGetValue(element, out var textAnnotation))
        {
            _textAnnotations.Remove(textAnnotation);
            if (ReferenceEquals(_selectedTextAnnotation, textAnnotation))
            {
                _selectedTextAnnotation = null;
                _activeTextEditor = null;
            }
        }

        ShapeCanvas.Children.Remove(element);
    }

    private TextAnnotationVisual CreateTextAnnotation(System.Windows.Shapes.Rectangle previewRectangle)
    {
        var editor = new System.Windows.Controls.TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = System.Windows.Media.Brushes.Black,
            CaretBrush = System.Windows.Media.Brushes.Black,
            FontSize = GetTextFontSize(previewRectangle.Height),
            FontFamily = new System.Windows.Media.FontFamily("Arial"),
            FontWeight = FontWeights.Normal,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Top,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left
        };

        var outline = new Border
        {
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };

        var resizeThumb = new Thumb
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.SizeNWSE,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 20, 20, 20)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };

        var container = new Grid
        {
            Width = Math.Max(40, previewRectangle.Width),
            Height = Math.Max(28, previewRectangle.Height),
            Background = System.Windows.Media.Brushes.Transparent
        };

        editor.PreviewMouseLeftButtonDown += TextAnnotation_OnPreviewMouseLeftButtonDown;
        resizeThumb.DragDelta += TextAnnotationResizeThumb_OnDragDelta;
        container.Children.Add(outline);
        container.Children.Add(editor);
        container.Children.Add(resizeThumb);
        container.MouseLeftButtonDown += TextAnnotationContainer_OnMouseLeftButtonDown;

        Canvas.SetLeft(container, Canvas.GetLeft(previewRectangle));
        Canvas.SetTop(container, Canvas.GetTop(previewRectangle));

        return new TextAnnotationVisual
        {
            Container = container,
            Outline = outline,
            Editor = editor,
            ResizeThumb = resizeThumb
        };
    }

    private void EraseStrokeAt(System.Windows.Point position)
    {
        var hitStrokes = AnnotationCanvas.Strokes.HitTest(position, 10);
        if (hitStrokes.Count == 0)
        {
            return;
        }

        for (var index = hitStrokes.Count - 1; index >= 0; index--)
        {
            var stroke = hitStrokes[index];
            AnnotationCanvas.Strokes.Remove(stroke);
            RemoveStrokeFromHistory(stroke);
        }
    }

    private void RemoveStrokeFromHistory(Stroke stroke)
    {
        for (var index = _annotationHistory.Count - 1; index >= 0; index--)
        {
            var action = _annotationHistory[index];
            if (action.Kind == AnnotationActionKind.Stroke && ReferenceEquals(action.Stroke, stroke))
            {
                _annotationHistory.RemoveAt(index);
                _redoHistory.Clear();
                return;
            }
        }
    }

    private void CommitActiveTextEditor()
    {
        if (_activeTextEditor is null)
        {
            return;
        }

        _activeTextEditor.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    private void CancelActiveTextEditor()
    {
        if (_activeTextEditor is null)
        {
            return;
        }

        _activeTextEditor = null;
        SelectTextAnnotation(null);
    }

    private void TextAnnotationContainer_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid container || !_textAnnotationLookup.TryGetValue(container, out var annotation))
        {
            return;
        }

        SelectTextAnnotation(annotation);
        e.Handled = true;
    }

    private void TextAnnotation_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox editor)
        {
            return;
        }

        var annotation = _textAnnotations.FirstOrDefault(candidate => ReferenceEquals(candidate.Editor, editor));
        if (annotation is not null)
        {
            SelectTextAnnotation(annotation);
        }
    }

    private void TextAnnotationResizeThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var annotation = _textAnnotations.FirstOrDefault(candidate => ReferenceEquals(candidate.ResizeThumb, sender));
        if (annotation is null)
        {
            return;
        }

        annotation.Container.Width = Math.Max(60, annotation.Container.Width + e.HorizontalChange);
        annotation.Container.Height = Math.Max(28, annotation.Container.Height + e.VerticalChange);
        annotation.Editor.FontSize = GetTextFontSize(annotation.Container.Height);
    }

    private void SelectTextAnnotation(TextAnnotationVisual? annotation)
    {
        if (ReferenceEquals(_selectedTextAnnotation, annotation))
        {
            return;
        }

        if (_selectedTextAnnotation is not null)
        {
            _selectedTextAnnotation.Outline.Visibility = Visibility.Collapsed;
            _selectedTextAnnotation.ResizeThumb.Visibility = Visibility.Collapsed;
        }

        _selectedTextAnnotation = annotation;
        _activeTextEditor = annotation?.Editor;

        if (_selectedTextAnnotation is not null)
        {
            _selectedTextAnnotation.Outline.Visibility = Visibility.Visible;
            _selectedTextAnnotation.ResizeThumb.Visibility = Visibility.Visible;
        }
    }

    private static double GetTextFontSize(double containerHeight)
    {
        return Math.Max(12, Math.Min(64, containerHeight * 0.45));
    }

    private static System.Windows.Size GetElementSize(FrameworkElement element)
    {
        if (element.ActualWidth > 0 && element.ActualHeight > 0)
        {
            return new System.Windows.Size(element.ActualWidth, element.ActualHeight);
        }

        element.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize;
    }

    private void ApplySnapGuides(ref double left, ref double top, ref double width, ref double height, bool snapLeft = false, bool snapTop = false, bool snapRight = false, bool snapBottom = false)
    {
        if (_hwndSource is null || _isAnnotating)
        {
            HideSnapGuides();
            return;
        }

        var area = System.Windows.Forms.Screen.FromHandle(_hwndSource.Handle).WorkingArea;
        double? verticalGuide = null;
        double? horizontalGuide = null;

        var screenLeft = (double)area.Left;
        var screenRight = area.Right;
        var screenTop = (double)area.Top;
        var screenBottom = area.Bottom;
        var screenCenterX = area.Left + area.Width / 2d;
        var screenCenterY = area.Top + area.Height / 2d;

        if (snapLeft && snapRight)
        {
            var currentLeft = left + FrameInteractionPadding;
            var currentRight = left + width - FrameInteractionPadding;
            var currentCenter = left + width / 2d;
            SnapHorizontal(ref left, currentLeft, [screenLeft, screenCenterX, screenRight], ref verticalGuide);
            SnapHorizontal(ref left, currentRight, [screenLeft, screenCenterX, screenRight], ref verticalGuide);
            SnapHorizontal(ref left, currentCenter, [screenLeft, screenCenterX, screenRight], ref verticalGuide);
        }
        else if (snapLeft)
        {
            var currentLeft = left + FrameInteractionPadding;
            SnapHorizontal(ref left, currentLeft, [screenLeft, screenCenterX, screenRight], ref verticalGuide);
        }
        else if (snapRight)
        {
            var currentRight = left + width - FrameInteractionPadding;
            var best = FindSnapOffset(currentRight, [screenLeft, screenCenterX, screenRight], out var guideX);
            if (best.HasValue)
            {
                width += best.Value;
                verticalGuide = guideX;
            }
        }

        if (snapTop && snapBottom)
        {
            var currentTop = top + FrameInteractionPadding;
            var currentBottom = top + height - FrameInteractionPadding;
            var currentCenter = top + height / 2d;
            SnapVertical(ref top, currentTop, [screenTop, screenCenterY, screenBottom], ref horizontalGuide);
            SnapVertical(ref top, currentBottom, [screenTop, screenCenterY, screenBottom], ref horizontalGuide);
            SnapVertical(ref top, currentCenter, [screenTop, screenCenterY, screenBottom], ref horizontalGuide);
        }
        else if (snapTop)
        {
            var currentTop = top + FrameInteractionPadding;
            SnapVertical(ref top, currentTop, [screenTop, screenCenterY, screenBottom], ref horizontalGuide);
        }
        else if (snapBottom)
        {
            var currentBottom = top + height - FrameInteractionPadding;
            var best = FindSnapOffset(currentBottom, [screenTop, screenCenterY, screenBottom], out var guideY);
            if (best.HasValue)
            {
                height += best.Value;
                horizontalGuide = guideY;
            }
        }

        ShowSnapGuides(verticalGuide, horizontalGuide);
    }

    private static void SnapHorizontal(ref double left, double currentValue, double[] targetValues, ref double? guide)
    {
        var best = FindSnapOffset(currentValue, targetValues, out var guideX);
        if (best.HasValue)
        {
            left += best.Value;
            guide = guideX;
        }
    }

    private static void SnapVertical(ref double top, double currentValue, double[] targetValues, ref double? guide)
    {
        var best = FindSnapOffset(currentValue, targetValues, out var guideY);
        if (best.HasValue)
        {
            top += best.Value;
            guide = guideY;
        }
    }

    private static double? FindSnapOffset(double currentValue, double[] targets, out double? guide)
    {
        double? bestOffset = null;
        guide = null;

        foreach (var target in targets)
        {
            var offset = target - currentValue;
            if (Math.Abs(offset) > SnapThreshold)
            {
                continue;
            }

            if (bestOffset is null || Math.Abs(offset) < Math.Abs(bestOffset.Value))
            {
                bestOffset = offset;
                guide = target;
            }
        }

        return bestOffset;
    }

    private void ShowSnapGuides(double? screenX, double? screenY)
    {
        if (screenX.HasValue)
        {
            VerticalSnapGuide.Opacity = 1;
            VerticalSnapGuide.Margin = new Thickness(screenX.Value - Left, 0, 0, 0);
        }
        else
        {
            VerticalSnapGuide.Opacity = 0;
        }

        if (screenY.HasValue)
        {
            HorizontalSnapGuide.Opacity = 1;
            HorizontalSnapGuide.Margin = new Thickness(0, screenY.Value - Top, 0, 0);
        }
        else
        {
            HorizontalSnapGuide.Opacity = 0;
        }
    }

    private void HideSnapGuides()
    {
        VerticalSnapGuide.Opacity = 0;
        HorizontalSnapGuide.Opacity = 0;
    }


    private void LeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeLeft(e.HorizontalChange);
    }

    private void RightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = Left;
        var top = Top;
        var width = Math.Max(MinimumFrameWidth, Width + e.HorizontalChange);
        var height = Height;

        ApplySnapGuides(ref left, ref top, ref width, ref height, snapRight: true);
        Width = width;
    }

    private void TopThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeTop(e.VerticalChange);
    }

    private void BottomThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = Left;
        var top = Top;
        var width = Width;
        var height = Math.Max(MinimumFrameHeight, Height + e.VerticalChange);

        ApplySnapGuides(ref left, ref top, ref width, ref height, snapBottom: true);
        Height = height;
    }

    private void TopLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeLeft(e.HorizontalChange);
        ResizeTop(e.VerticalChange);
    }

    private void TopRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = Left;
        var top = Top;
        var width = Math.Max(MinimumFrameWidth, Width + e.HorizontalChange);
        var height = Height;
        ApplySnapGuides(ref left, ref top, ref width, ref height, snapRight: true);
        Width = width;
        ResizeTop(e.VerticalChange);
    }

    private void BottomLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeLeft(e.HorizontalChange);
        var left = Left;
        var top = Top;
        var width = Width;
        var height = Math.Max(MinimumFrameHeight, Height + e.VerticalChange);
        ApplySnapGuides(ref left, ref top, ref width, ref height, snapBottom: true);
        Height = height;
    }

    private void BottomRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = Left;
        var top = Top;
        var width = Math.Max(MinimumFrameWidth, Width + e.HorizontalChange);
        var height = Math.Max(MinimumFrameHeight, Height + e.VerticalChange);
        ApplySnapGuides(ref left, ref top, ref width, ref height, snapRight: true, snapBottom: true);
        Width = width;
        Height = height;
    }

    private void ResizeLeft(double horizontalChange)
    {
        var left = Left;
        var top = Top;
        var width = Width - horizontalChange;
        var height = Height;
        if (width < MinimumFrameWidth)
        {
            horizontalChange = Width - MinimumFrameWidth;
            width = MinimumFrameWidth;
        }

        left += horizontalChange;
        ApplySnapGuides(ref left, ref top, ref width, ref height, snapLeft: true);

        Left = left;
        Width = width;
    }

    private void ResizeTop(double verticalChange)
    {
        var left = Left;
        var top = Top;
        var width = Width;
        var height = Height - verticalChange;
        if (height < MinimumFrameHeight)
        {
            verticalChange = Height - MinimumFrameHeight;
            height = MinimumFrameHeight;
        }

        top += verticalChange;
        ApplySnapGuides(ref left, ref top, ref width, ref height, snapTop: true);

        Top = top;
        Height = height;
    }
}
