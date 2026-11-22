using System.Diagnostics;

namespace GameDesk;

internal sealed class ControlWindow : Form
{
    internal const string AppName = "eFootball Net";
    private static readonly Color HeaderColor = Color.FromArgb(18, 38, 57);
    private static readonly Color ActiveTabColor = Color.FromArgb(42, 77, 108);
    private static readonly Color AccentColor = Color.FromArgb(21, 104, 160);
    private static readonly Color TextColor = Color.FromArgb(30, 45, 60);
    private static readonly Color SecondaryColor = Color.FromArgb(89, 105, 122);
    private readonly Font headingFont = new("Segoe UI Semibold", 16, FontStyle.Regular);
    private readonly Font readingFont = new("Segoe UI Semibold", 14, FontStyle.Regular);
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = TextColor };
    private readonly Button monitor = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly CheckBox overlay = new() { AutoSize = true };
    private readonly CheckBox details = new() { AutoSize = true };
    private readonly Button mark = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button monitorTab = new() { Text = "Monitor", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button historyTab = new() { Text = "History", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Panel workspace = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label endpoint = ReadoutValue();
    private readonly Label udp = ReadoutValue();
    private readonly Label rtt = ReadoutValue();
    private readonly Label gateway = ReadoutValue();
    private readonly LiveGapChart chart = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
    private readonly TableLayoutPanel dashboard;
    private readonly string logFolder;
    private ReviewView? history;
    private bool historySelected;
    private NetworkSample? live;
    private string? provider;

    public ControlWindow(Action toggleMonitor, Action toggleOverlay, Action toggleDetails, Action markLag,
        bool hasOverlayHotkey, string? detailShortcut, bool hasMarkerHotkey, string? logFolder = null)
    {
        this.logFolder = logFolder ?? SessionLog.Folder;
        Text = AppName;
        Icon = SystemIcons.Information;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(860, 590);
        BackColor = Color.FromArgb(241, 245, 249);

        monitor.Click += (_, _) => toggleMonitor();
        monitor.BackColor = AccentColor;
        monitor.ForeColor = Color.White;
        monitor.FlatStyle = FlatStyle.Flat;
        monitor.FlatAppearance.BorderSize = 0;
        monitor.Padding = new Padding(10, 4, 10, 4);

        overlay.Text = hasOverlayHotkey ? "Show overlay (Ctrl+Alt+F9)" : "Show overlay";
        overlay.AutoCheck = false;
        overlay.Click += (_, _) => toggleOverlay();
        details.Text = detailShortcut is null ? "Overlay charts" : $"Overlay charts ({detailShortcut})";
        details.AutoCheck = false;
        details.Click += (_, _) => toggleDetails();
        mark.Text = hasMarkerHotkey ? "Mark felt lag (Ctrl+Alt+F10)" : "Mark felt lag";
        mark.Click += (_, _) => markLag();
        mark.Padding = new Padding(6, 3, 6, 3);

        monitorTab.Click += (_, _) => ShowMonitor();
        historyTab.Click += (_, _) => ShowHistory();
        foreach (var tab in new[] { monitorTab, historyTab })
        {
            tab.FlatStyle = FlatStyle.Flat;
            tab.FlatAppearance.BorderSize = 0;
            tab.ForeColor = Color.White;
            tab.Padding = new Padding(14, 5, 14, 5);
        }

        var banner = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            Padding = new Padding(17, 9, 13, 9), Margin = Padding.Empty, BackColor = HeaderColor,
            ColumnCount = 3, RowCount = 1
        };
        banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        banner.Controls.Add(new Label
        {
            Text = AppName, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.White, Font = headingFont
        }, 0, 0);
        banner.Controls.Add(monitorTab, 1, 0);
        banner.Controls.Add(historyTab, 2, 0);

        dashboard = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16, 13, 16, 14), ColumnCount = 1, RowCount = 5
        };
        dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            dashboard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dashboard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        status.Margin = new Padding(0, 0, 0, 7);
        dashboard.Controls.Add(status, 0, 0);
        dashboard.Controls.Add(ActionRow(monitor, mark), 0, 1);
        dashboard.Controls.Add(ActionRow(overlay, details), 0, 2);

        var cards = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 6, 0, 0)
        };
        for (int i = 0; i < cards.ColumnCount; i++)
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cards.Controls.Add(Card("Match region", endpoint), 0, 0);
        cards.Controls.Add(Card("UDP gap", udp), 1, 0);
        cards.Controls.Add(Card("Match RTT", rtt), 2, 0);
        cards.Controls.Add(Card("Gateway RTT", gateway), 3, 0);
        dashboard.Controls.Add(cards, 0, 3);
        dashboard.Controls.Add(chart, 0, 4);
        workspace.Controls.Add(dashboard);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(banner, 0, 0);
        layout.Controls.Add(workspace, 0, 1);
        Controls.Add(layout);
        Resize += (_, _) => FitStatus();
        VisibleChanged += (_, _) => UpdateReadouts();
        ShowMonitor();
    }

    private static Label ReadoutValue() => new()
    {
        Text = "—", AutoEllipsis = true, Dock = DockStyle.Top, Height = 35,
        TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextColor
    };

    private Control Card(string name, Label value)
    {
        var card = new Panel
        {
            BackColor = Color.White, Padding = new Padding(10, 7, 10, 6), Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 0)
        };
        var caption = new Label { Text = name, Dock = DockStyle.Top, ForeColor = SecondaryColor };
        value.Font = readingFont;
        value.AccessibleName = name;
        card.Controls.Add(value);
        card.Controls.Add(caption);
        card.HandleCreated += (_, _) => SizeCard();
        card.DpiChangedAfterParent += (_, _) => SizeCard();
        SizeCard();
        return card;

        void SizeCard()
        {
            caption.Height = TextRenderer.MeasureText(name, caption.Font).Height + 2;
            value.Height = TextRenderer.MeasureText("000 ms", value.Font).Height + 2;
            card.MinimumSize = new Size(0, caption.Height + value.Height + card.Padding.Vertical);
        }
    }

    private static Control ActionRow(params Control[] actions)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = new Padding(0, 0, 0, 4)
        };
        row.Controls.AddRange(actions);
        return row;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var area = Screen.FromHandle(Handle).WorkingArea;
        float scale = DeviceDpi / 96f;
        Size = new Size(Math.Min(area.Width, (int)(875 * scale)), Math.Min(area.Height, (int)(620 * scale)));
        MinimumSize = new Size(Math.Min(area.Width, 640), Math.Min(area.Height, 430));
        FitStatus();
    }

    private void FitStatus() => status.MaximumSize = new Size(Math.Max(80, ClientSize.Width - 48), 0);

    protected override bool ShowWithoutActivation => !ShowInTaskbar;

    private void ShowMonitor()
    {
        historySelected = false;
        history?.Hide();
        dashboard.Show();
        monitorTab.BackColor = ActiveTabColor;
        historyTab.BackColor = HeaderColor;
        AcceptButton = monitor;
        UpdateReadouts();
    }

    public void ShowHistory()
    {
        if (history is null)
        {
            history = new ReviewView(logFolder);
            workspace.Controls.Add(history);
        }
        historySelected = true;
        dashboard.Hide();
        history.Show();
        history.BringToFront();
        monitorTab.BackColor = HeaderColor;
        historyTab.BackColor = ActiveTabColor;
        AcceptButton = null;
    }

    public void UpdateStatus(string message, string monitorText, bool canMonitor, bool showOverlay, bool showDetails, bool canMark,
        NetworkSample? sample = null, string? region = null)
    {
        status.Text = message;
        monitor.Text = monitorText;
        monitor.Enabled = canMonitor;
        overlay.Checked = showOverlay;
        details.Checked = showDetails;
        details.Enabled = showOverlay;
        mark.Enabled = canMark;
        live = sample;
        provider = region;
        UpdateReadouts();
    }

    private void UpdateReadouts()
    {
        if (!Visible || historySelected)
            return;
        endpoint.Text = provider?.Replace("Google Cloud ·", "GCP ·", StringComparison.Ordinal) ?? "—";
        udp.Text = SessionReview.Ms(live?.Status == "spike" ? live.Spike?.Ms : live?.BaselineMs);
        udp.ForeColor = live?.Status == "spike" ? Color.FromArgb(169, 78, 15) : TextColor;
        rtt.Text = ProbeText(live?.Probes?.Match);
        gateway.Text = ProbeText(live?.Probes?.Gateway);
        chart.ShowSample(live);
    }

    private string ProbeText(PingResult? probe)
    {
        if (probe is null || live is null || live.Qpc - probe.Qpc > 5 * Stopwatch.Frequency)
            return "—";
        return probe.Ms is long ms ? $"{ms} ms" : probe.Status == "TimedOut" ? "Timeout" : "—";
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            headingFont.Dispose();
            readingFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
