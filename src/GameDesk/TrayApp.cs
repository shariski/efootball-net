using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameDesk;

internal sealed class TrayApp : ApplicationContext
{
    private readonly Overlay overlay = new();
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem monitorItem;
    private readonly ToolStripMenuItem overlayItem;
    private readonly ToolStripMenuItem detailsItem;
    private readonly ToolStripMenuItem markerItem;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly bool demo;
    private readonly Task<ProviderCatalog> providers = Task.Run(ProviderCatalog.Load);
    private ControlWindow controls;
    private NamedPipeServerStream? pipe;
    private SessionLog? log;
    private NetworkSample? sample;
    private long marker;
    private long demoStarted;
    private int foregroundPid;
    private bool foregroundIsGame;
    private bool collectorConnected;
    private string? notice;
    private string? labeledEndpoint;
    private string? endpointLabel;

    public TrayApp(bool demo)
    {
        this.demo = demo;
        var preferences = OverlaySettings.Read(OverlaySettings.Path);
        overlay.Detailed = preferences.Detailed;
        var menu = new ContextMenuStrip();
        monitorItem = new ToolStripMenuItem("Start network monitor", null, (_, _) => ToggleMonitor());
        overlayItem = new ToolStripMenuItem("Show in-game overlay", null, (_, _) => ToggleOverlay())
            { Checked = preferences.Visible };
        detailsItem = new ToolStripMenuItem("Detailed overlay", null, (_, _) => ToggleDetails())
            { Checked = overlay.Detailed };
        markerItem = new ToolStripMenuItem("Mark felt lag", null, (_, _) => Mark()) { Enabled = demo };
        menu.Items.Add(new ToolStripMenuItem("Open eFootball Net", null, (_, _) => OpenControls()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(monitorItem);
        menu.Items.Add(overlayItem);
        menu.Items.Add(detailsItem);
        menu.Items.Add(markerItem);
        menu.Items.Add(new ToolStripMenuItem("History", null, (_, _) => OpenReview()));
        menu.Items.Add(new ToolStripMenuItem("Open session logs", null, (_, _) => OpenLogs()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        tray = new NotifyIcon { Icon = SystemIcons.Information, ContextMenuStrip = menu, Text = ControlWindow.AppName, Visible = true };

        _ = overlay.Handle;
        overlay.ToggleRequested += ToggleOverlay;
        overlay.MarkerRequested += Mark;
        overlay.DetailRequested += ToggleDetails;
        if (overlay.DetailShortcut is { } detailShortcut)
            detailsItem.ShortcutKeyDisplayString = detailShortcut;
        if (overlay.ToggleRegistered)
            overlayItem.ShortcutKeyDisplayString = "Ctrl+Alt+F9";
        if (overlay.MarkerRegistered)
            markerItem.ShortcutKeyDisplayString = "Ctrl+Alt+F10";
        controls = CreateControls();
        _ = controls.Handle;
        tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenControls(); };
        timer.Tick += (_, _) => RefreshOverlay();
        if (demo)
        {
            overlayItem.Checked = true;
            monitorItem.Enabled = false;
            demoStarted = Stopwatch.GetTimestamp();
            timer.Start();
            RefreshOverlay();
        }
    }

    public void OpenControls()
    {
        if (controls.IsDisposed)
            controls = CreateControls();
        RefreshControls();
        if (!controls.Visible)
            controls.Show();
        if (controls.WindowState == FormWindowState.Minimized)
            controls.WindowState = FormWindowState.Normal;
        controls.Activate();
    }

    public void RequestOpenControls()
    {
        try
        {
            overlay.BeginInvoke(OpenControls);
        }
        catch (InvalidOperationException)
        {
            // The app is closing before the request can be posted.
        }
    }

    private ControlWindow CreateControls() =>
        new(ToggleMonitor, ToggleOverlay, ToggleDetails, Mark,
            overlay.ToggleRegistered, overlay.DetailShortcut, overlay.MarkerRegistered);

    private void OpenReview()
    {
        OpenControls();
        controls.ShowHistory();
    }

    private void RefreshControls()
    {
        if (controls.IsDisposed)
            return;
        markerItem.Enabled = demo || log is not null;
        string message = notice ?? (demo ? "Overlay demo is running." : pipe is null
            ? "Network monitor is off. Start it before you enter a match."
            : !collectorConnected ? "Starting network monitor. Check the Windows permission prompt."
            : sample?.Status switch
            {
                "waiting-game" => "Monitoring is active. Waiting for eFootball.",
                "waiting-traffic" => "eFootball detected. Waiting for match traffic.",
                "trace-loss" => "Windows missed network trace events. Waiting for fresh data.",
                "insufficient" => "Match connection detected. Waiting for fresh UDP samples.",
                "spike" => "Match connection detected. Recent UDP gap spike.",
                "stable" => "Match connection detected. UDP arrivals are steady.",
                _ => "Connected to the network collector."
            });
        controls.UpdateStatus(message, monitorItem.Text ?? "Start network monitor",
            monitorItem.Enabled, overlayItem.Checked, overlay.Detailed, markerItem.Enabled, sample,
            ProviderFor(sample?.Endpoint));
    }

    private void ToggleMonitor()
    {
        if (pipe is not null)
        {
            StopMonitor();
            return;
        }
        _ = StartMonitor();
    }

    private async Task StartMonitor()
    {
        NamedPipeServerStream? server = null;
        try
        {
            notice = null;
            server = Collector.CreateServer(out string token);
            pipe = server;
            monitorItem.Text = "Stop network monitor";
            sample = new(Stopwatch.GetTimestamp(), "waiting-game");
            timer.Start();
            RefreshControls();
            controls.Update();
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--collect {token} {Environment.ProcessId}"
            };
            using var worker = Process.Start(start);
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await server.WaitForConnectionAsync(connectTimeout.Token);
            if (pipe != server)
                return;
            collectorConnected = true;
            RefreshControls();
            using var reader = new StreamReader(server);
            string? line;
            while (pipe == server && (line = await reader.ReadLineAsync()) is not null)
            {
                var reading = JsonSerializer.Deserialize<NetworkSample>(line);
                if (reading is null)
                    continue;
                if (reading.Error is { } error)
                {
                    Notify("Network monitor unavailable", error);
                    break;
                }
                sample = reading;
                Record(reading);
                RefreshControls();
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            Notify("Network monitor paused", "Windows permission was declined.");
        }
        catch (Exception error)
        {
            if (server is null || pipe == server)
                Notify("Network monitor unavailable", error.GetBaseException().Message);
        }
        finally
        {
            if (pipe == server)
                StopMonitor();
            server?.Dispose();
        }
    }

    private void StopMonitor()
    {
        var previous = pipe;
        pipe = null;
        previous?.Dispose();
        CloseLog();
        collectorConnected = false;
        sample = null;
        marker = 0;
        timer.Stop();
        overlay.Hide();
        monitorItem.Text = "Start network monitor";
        RefreshControls();
    }

    private void ToggleOverlay()
    {
        overlayItem.Checked = !overlayItem.Checked;
        SaveOverlaySettings();
        RefreshControls();
        RefreshOverlay();
    }

    private void ToggleDetails()
    {
        overlayItem.Checked = true;
        overlay.Detailed = !overlay.Detailed;
        detailsItem.Checked = overlay.Detailed;
        SaveOverlaySettings();
        RefreshControls();
        RefreshOverlay();
    }

    private void SaveOverlaySettings()
    {
        if (demo)
            return;
        try
        {
            new OverlaySettings(overlayItem.Checked, overlay.Detailed).Save(OverlaySettings.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify("Unable to save overlay settings", error.Message);
        }
    }

    private string? ProviderFor(string? endpoint)
    {
        if (endpoint is null || !providers.IsCompletedSuccessfully)
            return null;
        if (endpoint != labeledEndpoint)
        {
            labeledEndpoint = endpoint;
            endpointLabel = providers.Result.Resolve(endpoint);
        }
        return endpointLabel;
    }

    private void RefreshOverlay()
    {
        if (!overlayItem.Checked || (!demo && pipe is null))
        {
            overlay.Hide();
            return;
        }
        nint active = GetForegroundWindow();
        if (!demo && !IsGame(active))
        {
            overlay.Hide();
            return;
        }

        overlay.MoveTo(Screen.FromHandle(active).Bounds);
        var reading = demo ? DemoReading() : sample;
        if (reading is not null)
            overlay.UpdateReading(reading, marker, demo ? "Demo · AWS · Jakarta" : ProviderFor(reading.Endpoint));
        if (!overlay.Visible)
            overlay.Show();
    }

    private bool IsGame(nint active)
    {
        _ = GetWindowThreadProcessId(active, out int pid);
        if (pid == foregroundPid)
            return foregroundIsGame;
        foregroundPid = pid;
        foregroundIsGame = false;
        if (pid == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return foregroundIsGame = process.ProcessName.Equals("eFootball", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private void Record(NetworkSample reading)
    {
        if (reading.Endpoint is null && reading.Status != "trace-loss")
        {
            CloseLog();
            return;
        }
        try
        {
            log ??= new SessionLog();
            log.Record(reading);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CloseLog();
            Notify("Session log unavailable", error.Message);
            StopMonitor();
        }
    }

    private void Mark()
    {
        if (!demo && log is null)
            return;
        marker = Stopwatch.GetTimestamp();
        try
        {
            log?.Mark(marker);
        }
        catch (IOException error)
        {
            Notify("Unable to save felt-lag marker", error.Message);
            StopMonitor();
        }
        RefreshOverlay();
    }

    private void CloseLog()
    {
        var previous = log;
        log = null;
        try
        {
            previous?.Dispose();
        }
        catch (IOException error)
        {
            Notify("Unable to finish session log", error.Message);
        }
    }

    private NetworkSample DemoReading()
    {
        long now = Stopwatch.GetTimestamp();
        long stride = Stopwatch.Frequency / 4;
        long elapsed = (now - demoStarted) / stride;
        long turn = elapsed / 48;
        long phase = elapsed % 48;
        var points = Enumerable.Range(0, 60).Select(i =>
        {
            long position = elapsed - 59 + i;
            double ms = position >= 0 && position % 48 == 22 ? 118 : 17 + Math.Abs(position * 13 % 9);
            return new GapPoint(now - (59 - i) * stride, ms);
        }).ToArray();
        var spike = phase is >= 22 and < 38
            ? new GapPoint(demoStarted + (turn * 48 + 22) * stride, 118) : null;
        long probeTime = demoStarted + elapsed / 8 * 8 * stride;
        return new(now, spike is null ? "stable" : "spike", BaselineMs: 21, Spike: spike, Points: points,
            Probes: new(new(probeTime, phase / 8 == 3 ? 50 : 31, "Success"), new(probeTime, 2, "Success")));
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(SessionLog.Folder);
            Process.Start(new ProcessStartInfo(SessionLog.Folder) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Notify("Unable to open session logs", error.Message);
        }
    }

    private void Notify(string title, string body)
    {
        notice = body;
        RefreshControls();
        tray.ShowBalloonTip(4000, title, body, ToolTipIcon.Warning);
    }

    protected override void ExitThreadCore()
    {
        StopMonitor();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        timer.Dispose();
        overlay.Dispose();
        controls.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

}
