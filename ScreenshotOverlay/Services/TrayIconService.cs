namespace ScreenshotOverlay.Services;

public sealed class TrayFrameItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
}

public sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _newFrameItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _framesRootItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _exitItem;

    public event EventHandler? NewFrameRequested;
    public event EventHandler<string>? ToggleFrameRequested;
    public event EventHandler<string>? CaptureFrameRequested;
    public event EventHandler<string>? CloseFrameRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(System.Drawing.Icon icon)
    {
        _menu = new System.Windows.Forms.ContextMenuStrip();
        _newFrameItem = new System.Windows.Forms.ToolStripMenuItem("New Frame", null, (_, _) => NewFrameRequested?.Invoke(this, EventArgs.Empty));
        _framesRootItem = new System.Windows.Forms.ToolStripMenuItem("Frames");
        _exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _menu.Items.Add(_newFrameItem);
        _menu.Items.Add(_framesRootItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "FrameSnip",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.DoubleClick += (_, _) => NewFrameRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void UpdateFrames(IReadOnlyList<TrayFrameItem> frames)
    {
        _framesRootItem.DropDownItems.Clear();

        if (frames.Count == 0)
        {
            var emptyItem = new System.Windows.Forms.ToolStripMenuItem("No open frames")
            {
                Enabled = false
            };
            _framesRootItem.DropDownItems.Add(emptyItem);
            return;
        }

        foreach (var frame in frames)
        {
            var frameMenu = new System.Windows.Forms.ToolStripMenuItem(frame.Label);
            frameMenu.DropDownItems.Add("Show / Focus", null, (_, _) => ToggleFrameRequested?.Invoke(this, frame.Id));
            frameMenu.DropDownItems.Add("Capture", null, (_, _) => CaptureFrameRequested?.Invoke(this, frame.Id));
            frameMenu.DropDownItems.Add("Close", null, (_, _) => CloseFrameRequested?.Invoke(this, frame.Id));
            _framesRootItem.DropDownItems.Add(frameMenu);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _menu.Dispose();
        _notifyIcon.Dispose();
    }
}
