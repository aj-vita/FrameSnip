using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenshotOverlay.Interop;

namespace ScreenshotOverlay;

public partial class SelectionOverlayWindow : Window
{
    private System.Windows.Point? _dragStartPoint;
    private System.Windows.Point? _dragStartScreenPoint;
    private HwndSource? _hwndSource;

    public Int32Rect? SelectedBounds { get; private set; }

    public SelectionOverlayWindow()
    {
        InitializeComponent();

        var virtualScreen = NativeMethods.GetVirtualScreenRectangle();
        Left = virtualScreen.X;
        Top = virtualScreen.Y;
        Width = virtualScreen.Width;
        Height = virtualScreen.Height;

        Loaded += (_, _) =>
        {
            RootCanvas.Width = ActualWidth;
            RootCanvas.Height = ActualHeight;
        };
        SourceInitialized += (_, _) => _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
    }

    private void RootCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartScreenPoint = ScreenshotOverlay.Interop.NativeMethods.GetCursorScreenPosition();
        _dragStartPoint = ToLocalPoint(_dragStartScreenPoint.Value);
        UpdateSelection(_dragStartPoint.Value, _dragStartPoint.Value);
        SelectionFill.Visibility = Visibility.Visible;
        RootCanvas.CaptureMouse();
    }

    private void RootCanvas_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStartPoint is null || _dragStartScreenPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentScreenPoint = ScreenshotOverlay.Interop.NativeMethods.GetCursorScreenPosition();
        UpdateSelection(_dragStartPoint.Value, ToLocalPoint(currentScreenPoint));
    }

    private void RootCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStartPoint is null || _dragStartScreenPoint is null)
        {
            return;
        }

        var endScreenPoint = ScreenshotOverlay.Interop.NativeMethods.GetCursorScreenPosition();
        var endPoint = ToLocalPoint(endScreenPoint);
        UpdateSelection(_dragStartPoint.Value, endPoint);
        RootCanvas.ReleaseMouseCapture();

        var left = System.Windows.Controls.Canvas.GetLeft(SelectionFill);
        var top = System.Windows.Controls.Canvas.GetTop(SelectionFill);
        var width = SelectionFill.Width;
        var height = SelectionFill.Height;

        _dragStartPoint = null;
        var startScreenPoint = _dragStartScreenPoint.Value;
        _dragStartScreenPoint = null;

        if (width < 4 || height < 4)
        {
            SelectedBounds = null;
            DialogResult = false;
            Close();
            return;
        }

        var screenLeft = Math.Min(startScreenPoint.X, endScreenPoint.X);
        var screenTop = Math.Min(startScreenPoint.Y, endScreenPoint.Y);
        var screenRight = Math.Max(startScreenPoint.X, endScreenPoint.X);
        var screenBottom = Math.Max(startScreenPoint.Y, endScreenPoint.Y);

        SelectedBounds = new Int32Rect(
            (int)Math.Round(screenLeft),
            (int)Math.Round(screenTop),
            Math.Max(1, (int)Math.Round(screenRight - screenLeft)),
            Math.Max(1, (int)Math.Round(screenBottom - screenTop)));

        DialogResult = true;
        Close();
    }

    private void UpdateSelection(System.Windows.Point start, System.Windows.Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);

        System.Windows.Controls.Canvas.SetLeft(SelectionFill, left);
        System.Windows.Controls.Canvas.SetTop(SelectionFill, top);
        SelectionFill.Width = width;
        SelectionFill.Height = height;
    }

    private System.Windows.Point ToLocalPoint(System.Windows.Point screenPoint)
    {
        if (_hwndSource is not null)
        {
            var bounds = NativeMethods.GetWindowRectangle(_hwndSource.Handle);
            return new System.Windows.Point(screenPoint.X - bounds.X, screenPoint.Y - bounds.Y);
        }

        return new System.Windows.Point(screenPoint.X - Left, screenPoint.Y - Top);
    }
}
