using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GameDesk;

internal sealed class Overlay : Form
{
    private const int ToggleId = 1;
    private const int MarkerId = 2;
    private const int DetailId = 3;
    private const uint HotkeyModifiers = 0x4003; // Alt + Ctrl, no repeat.
    private const int HistorySeconds = 15;
    private const int StripHeight = 34;
    private const int CompactWidth = 280;
    private const int DetailedWidth = 408;
    private static readonly Color Warning = Color.FromArgb(255, 180, 110);
    private readonly Font providerFont = new("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font valueFont = new("Segoe UI Semibold", 15, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font chartFont = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Queue<PingResult> matchHistory = new();
    private readonly Queue<PingResult> gatewayHistory = new();
    private NetworkSample sample = new(0, "waiting-traffic");
    private long markedAt;
    private string? location;

    public event Action? ToggleRequested;
    public event Action? MarkerRequested;
    public event Action? DetailRequested;
    public bool ToggleRegistered { get; private set; }
    public bool MarkerRegistered { get; private set; }
    public string? DetailShortcut { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Detailed { get; set; }

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 25, 31);
        Opacity = 0.94;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new(CompactWidth, StripHeight);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var settings = base.CreateParams;
            settings.ExStyle |= 0x080000A0; // NOACTIVATE, TOOLWINDOW, TRANSPARENT.
            return settings;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ToggleRegistered = RegisterHotKey(Handle, ToggleId, HotkeyModifiers, (uint)Keys.F9);
        MarkerRegistered = RegisterHotKey(Handle, MarkerId, HotkeyModifiers, (uint)Keys.F10);
        DetailShortcut = RegisterHotKey(Handle, DetailId, HotkeyModifiers, (uint)Keys.F11) ? "Ctrl+Alt+F11"
            : RegisterHotKey(Handle, DetailId, HotkeyModifiers, (uint)Keys.F7) ? "Ctrl+Alt+F7" : null;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (ToggleRegistered)
            UnregisterHotKey(Handle, ToggleId);
        if (MarkerRegistered)
            UnregisterHotKey(Handle, MarkerId);
        if (DetailShortcut is not null)
            UnregisterHotKey(Handle, DetailId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312)
        {
            if (message.WParam == ToggleId)
                ToggleRequested?.Invoke();
            if (message.WParam == MarkerId)
                MarkerRequested?.Invoke();
            if (message.WParam == DetailId)
                DetailRequested?.Invoke();
        }
        base.WndProc(ref message);
    }

    public void UpdateReading(NetworkSample value, long marker, string? provider = null)
    {
        bool sameWaiting = marker == markedAt && value.Status == sample.Status &&
            value.Points is null && sample.Points is null && value.Error == sample.Error &&
            value.Probes == sample.Probes && provider == location;
        if (value.Endpoint != sample.Endpoint || value.LocalAddress != sample.LocalAddress)
        {
            matchHistory.Clear();
            gatewayHistory.Clear();
        }
        long cutoff = value.Qpc - HistorySeconds * Stopwatch.Frequency;
        RetainProbe(matchHistory, value.Probes?.Match, cutoff);
        RetainProbe(gatewayHistory, value.Probes?.Gateway, cutoff);
        sample = value;
        markedAt = marker;
        location = provider;
        if (Visible && !sameWaiting)
            Invalidate();
    }

    public void MoveTo(Rectangle area)
    {
        float scale = DeviceDpi / 96f;
        var target = new Size(Math.Min((int)Math.Round((Detailed ? DetailedWidth : CompactWidth) * scale),
            Math.Max(1, area.Width)), (int)Math.Round(StripHeight * scale));
        var bounds = new Rectangle(area.Right - target.Width, area.Top, target.Width, target.Height);
        if (Bounds != bounds)
            Bounds = bounds;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        float scale = DeviceDpi / 96f;
        graphics.ScaleTransform(scale, scale);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var indicator = new SolidBrush(StatusColor());
        using var text = new SolidBrush(Color.FromArgb(230, 237, 245));
        using var muted = new SolidBrush(Color.FromArgb(171, 185, 203));
        using var warning = new SolidBrush(Warning);
        using var divider = new Pen(Color.FromArgb(50, 61, 73));
        float width = ClientSize.Width / scale;
        var (server, region) = ProviderLabel(location);
        float serverWidth = graphics.MeasureString(server, providerFont).Width;
        float minimumHeader = Math.Min(99, width / 3);
        float header = Math.Clamp(serverWidth + 24, minimumHeader, width * 0.46f);
        float available = Math.Max(0, width - header - 4);
        graphics.FillEllipse(indicator, 8, 14, 6, 6);

        float providerWidth = header - 22;
        if (serverWidth <= providerWidth)
            graphics.DrawString(server, providerFont, text, 19, 9);
        else if (graphics.MeasureString(region, providerFont).Width <= providerWidth)
            graphics.DrawString(region, providerFont, text, 19, 9);
        else if (providerWidth > 0)
            graphics.DrawString(region, chartFont, text, new RectangleF(19, 3, providerWidth, StripHeight - 5));

        bool udpWarning = sample.Status == "spike";
        double? gapValue = udpWarning ? sample.Spike?.Ms : sample.BaselineMs;
        string gap = gapValue is double measured ? $"{measured:0}" : "—";
        var match = ProbeReading(sample.Probes?.Match, matchHistory, 15);
        var gateway = ProbeReading(sample.Probes?.Gateway, gatewayHistory, 5);
        float udpWidth = available * (Detailed ? 1f / 3 : 0.36f);
        float matchWidth = available * (Detailed ? 1f / 3 : 0.34f);

        var udpPlot = Metric("UDP", gap, udpWarning, header, udpWidth, Detailed);
        var matchPlot = Metric("RTT", match.Value, match.Bad, header + udpWidth, matchWidth, Detailed);
        var gatewayPlot = Metric("GW", gateway.Value, gateway.Bad, header + udpWidth + matchWidth,
            available - udpWidth - matchWidth, Detailed);
        if (!udpPlot.IsEmpty)
            PaintGaps(graphics, udpWarning ? Warning : Color.FromArgb(117, 221, 178), udpPlot);
        if (!matchPlot.IsEmpty)
            PaintProbes(graphics, matchHistory, match.Bad ? Warning : Color.FromArgb(127, 206, 241), matchPlot);
        if (!gatewayPlot.IsEmpty)
            PaintProbes(graphics, gatewayHistory, gateway.Bad ? Warning : Color.FromArgb(203, 174, 245), gatewayPlot);

        RectangleF Metric(string label, string value, bool bad, float left, float cell, bool chart)
        {
            graphics.DrawLine(divider, left, 6, left, 28);
            float labelWidth = graphics.MeasureString(label, chartFont).Width;
            float numberWidth = graphics.MeasureString(value, valueFont).Width;
            float start = left + 6;
            var valueBrush = bad ? warning : value == "—" ? muted : text;
            if (labelWidth + numberWidth + 10 > cell)
            {
                if (labelWidth + 6 <= cell)
                    graphics.DrawString(label, chartFont, muted, start, 1);
                if (numberWidth + 6 <= cell)
                    graphics.DrawString(value, valueFont, valueBrush, start, 13);
                return RectangleF.Empty;
            }

            graphics.DrawString(label, chartFont, muted, start, 10);
            float valueX = start + labelWidth + 2;
            graphics.DrawString(value, valueFont, valueBrush, valueX, 6);
            float plotLeft = valueX + numberWidth + 3;
            float plotWidth = left + cell - 4 - plotLeft;
            return chart && plotWidth >= 15 ? new RectangleF(plotLeft, 7, plotWidth, 20) : RectangleF.Empty;
        }
    }

    private Color StatusColor()
    {
        if (markedAt > 0 && Stopwatch.GetTimestamp() - markedAt < 2 * Stopwatch.Frequency)
            return Color.FromArgb(161, 200, 249);
        return sample.Status switch
        {
            "spike" or "trace-loss" or "error" => Warning,
            "stable" => Color.FromArgb(117, 221, 178),
            _ => Color.FromArgb(171, 185, 203)
        };
    }

    private static (string Server, string Region) ProviderLabel(string? provider)
    {
        if (provider is null or "Unknown provider")
            return ("—", "—");
        if (provider == "Local / private")
            return ("LAN", "LAN");
        if (provider.StartsWith("Demo ·", StringComparison.Ordinal))
            return ("Demo", "Demo");

        int separator = provider.IndexOf(" · ", StringComparison.Ordinal);
        if (separator < 0)
            return (provider, provider);
        string name = provider[..separator] == "Google Cloud" ? "GCP" : provider[..separator];
        string region = provider[(separator + 3)..];
        if (region == "Global edge")
            region = "Edge";
        return ($"{name} {region}", region);
    }

    private (string Value, bool Bad) ProbeReading(PingResult? probe, Queue<PingResult> history, int minimumRise)
    {
        if (probe is null || sample.Qpc - probe.Qpc > 5 * Stopwatch.Frequency)
            return ("—", false);
        if (probe.Ms is not long milliseconds)
            return probe.Status == "TimedOut" ? ("×", true) : ("—", false);
        return ($"{milliseconds}", ProbeIsHigh(milliseconds, history, probe.Qpc, minimumRise));
    }

    internal static bool ProbeIsHigh(long milliseconds, IEnumerable<PingResult> history, long currentQpc, int minimumRise)
    {
        var baseline = history.Where(reading => reading.Qpc < currentQpc && reading.Ms.HasValue)
            .Select(reading => reading.Ms.GetValueOrDefault()).Order().ToArray();
        if (baseline.Length < 3)
            return false;
        double typical = baseline[baseline.Length / 2];
        return milliseconds - typical >= Math.Max(minimumRise, typical * 0.4);
    }

    private static void RetainProbe(Queue<PingResult> history, PingResult? probe, long cutoff)
    {
        while (history.TryPeek(out var first) && first.Qpc < cutoff)
            history.Dequeue();
        if (probe is not null && probe.Qpc >= cutoff && (history.Count == 0 || probe.Qpc > history.Last().Qpc))
            history.Enqueue(probe);
    }

    private void PaintGaps(Graphics graphics, Color tint, RectangleF bounds)
    {
        if (sample.Points is not { Length: > 0 } points)
            return;
        double ceiling = Math.Max(60, Math.Max(3 * (sample.BaselineMs ?? 0),
            points.Max(point => point.Ms)));
        using var baseline = new Pen(Color.FromArgb(63, 75, 89));
        if (sample.BaselineMs is double typical)
        {
            float y = bounds.Bottom - (float)(typical / ceiling) * bounds.Height;
            graphics.DrawLine(baseline, bounds.Left, y, bounds.Right, y);
        }
        using var line = new Pen(tint, 1.25f);
        PointF? previous = null;
        long previousQpc = 0;
        foreach (var point in points)
        {
            float x = PlotX(point.Qpc, bounds);
            float y = bounds.Bottom - (float)(point.Ms / ceiling) * bounds.Height;
            if (previous is { } last && point.Qpc - previousQpc <= Stopwatch.Frequency / 2)
                graphics.DrawLine(line, last, new PointF(x, y));
            else
                graphics.DrawLine(line, x, y, x + 1, y);
            previous = new PointF(x, y);
            previousQpc = point.Qpc;
        }
    }

    private void PaintProbes(Graphics graphics, Queue<PingResult> history, Color tint, RectangleF bounds)
    {
        double ceiling = 10;
        foreach (var probe in history)
            ceiling = Math.Max(ceiling, probe.Ms ?? 0);

        using var floor = new Pen(Color.FromArgb(63, 75, 89));
        graphics.DrawLine(floor, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
        using var line = new Pen(tint, 1.25f);
        using var missing = new Pen(Warning, 1.1f);
        PointF? previous = null;
        long previousQpc = 0;
        foreach (var probe in history)
        {
            float x = PlotX(probe.Qpc, bounds);
            if (probe.Ms is not long ms)
            {
                previous = null;
                float middle = bounds.Top + bounds.Height / 2;
                graphics.DrawLine(missing, x - 2, middle - 2, x + 2, middle + 2);
                graphics.DrawLine(missing, x - 2, middle + 2, x + 2, middle - 2);
                continue;
            }
            float y = bounds.Bottom - (float)(ms / ceiling) * bounds.Height;
            if (previous is { } last && probe.Qpc - previousQpc <= 3.5 * Stopwatch.Frequency)
                graphics.DrawLine(line, last, new PointF(x, y));
            else
                graphics.DrawLine(line, x - 1, y, x + 1, y);
            previous = new PointF(x, y);
            previousQpc = probe.Qpc;
        }
    }

    private float PlotX(long qpc, RectangleF bounds)
    {
        double fraction = 1 - (sample.Qpc - qpc) / (double)(HistorySeconds * Stopwatch.Frequency);
        return bounds.Left + (float)Math.Clamp(fraction, 0, 1) * bounds.Width;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            providerFont.Dispose();
            valueFont.Dispose();
            chartFont.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
