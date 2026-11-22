namespace GameDesk;

internal sealed class ControlWindow : Form
{
    private readonly Label status = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button monitor = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly CheckBox overlay = new() { AutoSize = true };
    private readonly CheckBox details = new() { AutoSize = true };
    private readonly Button mark = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    public ControlWindow(Action toggleMonitor, Action toggleOverlay, Action toggleDetails, Action markLag, Action openReview,
        bool hasOverlayHotkey, string? detailShortcut, bool hasMarkerHotkey)
    {
        Text = "GameDesk";
        Icon = SystemIcons.Information;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        monitor.Click += (_, _) => toggleMonitor();
        overlay.Text = hasOverlayHotkey ? "Show in-game overlay (Ctrl+Alt+F9)" : "Show in-game overlay";
        overlay.AutoCheck = false;
        overlay.Click += (_, _) => toggleOverlay();
        details.Text = detailShortcut is null ? "Detailed overlay" : $"Detailed overlay ({detailShortcut})";
        details.AutoCheck = false;
        details.Click += (_, _) => toggleDetails();
        mark.Text = hasMarkerHotkey ? "Mark felt lag (Ctrl+Alt+F10)" : "Mark felt lag";
        mark.Click += (_, _) => markLag();
        var review = new Button { Text = "Session review", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        review.Click += (_, _) => openReview();

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < layout.RowCount; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty
        };
        actions.Controls.Add(mark);
        actions.Controls.Add(review);
        layout.Controls.Add(status, 0, 0);
        layout.Controls.Add(monitor, 0, 1);
        layout.Controls.Add(overlay, 0, 2);
        layout.Controls.Add(details, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        AcceptButton = monitor;
        SetStatusWidth(DeviceDpi);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetStatusWidth(DeviceDpi);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        SetStatusWidth(e.DeviceDpiNew);
    }

    private void SetStatusWidth(int dpi)
    {
        var width = (int)Math.Round(360 * dpi / 96.0);
        status.MaximumSize = new Size(width, 0);
        status.MinimumSize = new Size(width, 0);
    }

    protected override bool ShowWithoutActivation => !ShowInTaskbar;

    public void UpdateStatus(string message, string monitorText, bool canMonitor, bool showOverlay, bool showDetails, bool canMark)
    {
        status.Text = message;
        monitor.Text = monitorText;
        monitor.Enabled = canMonitor;
        overlay.Checked = showOverlay;
        details.Checked = showDetails;
        details.Enabled = showOverlay;
        mark.Enabled = canMark;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
