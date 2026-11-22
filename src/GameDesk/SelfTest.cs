using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace GameDesk;

internal static class SelfTest
{
    public static int CheckData(string report)
    {
        try
        {
            CheckReviewReader();
            CheckSettings();
            CheckFlow();
            if (!ProviderLabelsAreCorrect(ProviderCatalog.Load()))
                throw new InvalidDataException("Provider prefix checks failed.");
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            return 1;
        }
    }

    private static void CheckSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gamedesk-settings-{Guid.NewGuid():N}.json");
        try
        {
            if (OverlaySettings.Read(path) != new OverlaySettings())
                throw new InvalidDataException("A missing settings file should use the defaults.");
            var visible = new OverlaySettings(true, true);
            visible.Save(path);
            if (OverlaySettings.Read(path) != visible)
                throw new InvalidDataException("The overlay settings did not survive a restart.");
            new OverlaySettings(false, true).Save(path);
            if (OverlaySettings.Read(path) != new OverlaySettings(false, true))
                throw new InvalidDataException("The overlay settings did not overwrite the previous state.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CheckFlow()
    {
        var monitor = new NetworkMonitor();
        var local = IPAddress.Parse("192.0.2.2");
        var peer = IPAddress.Parse("198.51.100.8");
        long second = Stopwatch.Frequency;
        long arrival = 20 * second;
        monitor.SetTarget(12);
        for (int i = 0; i < 300; i++)
        {
            monitor.Observe(12, arrival, true, local, 6100, peer, 6200);
            monitor.Observe(12, arrival, false, local, 6100, peer, 6200);
            arrival += second / 50;
        }
        if (monitor.Sample(arrival, 0).Status != "stable")
            throw new InvalidDataException("Bidirectional fixture traffic did not reach a stable state.");
        arrival += second / 6;
        monitor.Observe(12, arrival, true, local, 6100, peer, 6200);
        if (monitor.Sample(arrival, 0) is not { Status: "spike", Spike.Ms: > 180 })
            throw new InvalidDataException("The fixture arrival gap was not detected.");
        if (monitor.Sample(arrival, 1).Status != "trace-loss")
            throw new InvalidDataException("A trace loss must invalidate the current flow.");
        monitor.SetTarget(0);
        if (monitor.Sample(arrival, 0) is not { Status: "waiting-game", EventsLost: 0 })
            throw new InvalidDataException("The loss counter must reset when the game closes.");
    }

    public static async Task<int> CheckCollectorAsync(string report)
    {
        try
        {
            using var server = Collector.CreateServer(out string token);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--collect");
            start.ArgumentList.Add(token);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            using var worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start collector.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.WaitForConnectionAsync(timeout.Token);
            NetworkSample? sample;
            using (var reader = new StreamReader(server))
            {
                string line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Collector sent no data.");
                sample = JsonSerializer.Deserialize<NetworkSample>(line);
            }
            await worker.WaitForExitAsync(timeout.Token);
            bool passed = sample is { Qpc: > 0, Error: null } && worker.ExitCode == 0;
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed, sample?.Status, sample?.Error,
                exitedAfterDisconnect = worker.HasExited, worker.ExitCode }));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            return 1;
        }
    }

    public static async Task<int> RunAsync(string report, bool ipv6)
    {
        var samples = new List<NetworkSample>();
        long pausedAt = 0;
        Process? echo = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            IPAddress loopback = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            string testPipe = $"GameDesk-Test-{Guid.NewGuid():N}";
            using var ready = new NamedPipeServerStream(testPipe, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--udp-echo");
            start.ArgumentList.Add(testPipe);
            start.ArgumentList.Add(ipv6 ? "ipv6" : "ipv4");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            echo = Process.Start(start) ?? throw new InvalidOperationException("Could not start UDP echo.");
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ready.WaitForConnectionAsync(startup.Token);
            using var portReader = new StreamReader(ready);
            int port = int.Parse(await portReader.ReadLineAsync(startup.Token) ??
                throw new IOException("UDP test listener did not return its port."));

            using var client = new UdpClient(new IPEndPoint(loopback, 0));
            client.Connect(loopback, port);
            var monitor = new NetworkMonitor();
            monitor.SetTarget(Environment.ProcessId);
            using var trace = new UdpTrace(monitor);
            long begin = Stopwatch.GetTimestamp();
            long nextSample = begin;
            while (Stopwatch.GetElapsedTime(begin).TotalSeconds < 13)
            {
                long now = Stopwatch.GetTimestamp();
                double seconds = Stopwatch.GetElapsedTime(begin, now).TotalSeconds;
                bool paused = seconds >= 7 && seconds < 7.65;
                if (paused && pausedAt == 0)
                    pausedAt = now;
                await client.SendAsync(paused ? new byte[] { 0 } : new byte[] { 1 }, timeout.Token);
                while (client.Available > 0)
                    await client.ReceiveAsync(timeout.Token);
                if (now >= nextSample)
                {
                    samples.Add(monitor.Sample(now, trace.EventsLost));
                    nextSample += Stopwatch.Frequency / 4;
                }
                await Task.Delay(20, timeout.Token);
            }
            var highlighted = samples.FirstOrDefault(sample => sample.Spike is { Ms: >= 500 and <= 950 });
            double? uiDelayMs = highlighted?.Spike is { } gap
                ? Stopwatch.GetElapsedTime(gap.Qpc, highlighted.Qpc).TotalMilliseconds : null;
            bool readyBeforePause = samples.Any(sample => sample.Qpc < pausedAt && sample.Status == "stable");
            bool recovered = samples.Last().Status == "stable";
            bool expectedEndpoint = highlighted?.Endpoint == new IPEndPoint(loopback, port).ToString();
            bool passed = readyBeforePause && recovered && expectedEndpoint && highlighted?.Status == "spike" &&
                samples.All(sample => sample.EventsLost == 0);
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                passed, ipv6, readyBeforePause, recovered, expectedEndpoint, uiDelayMs,
                measuredGapMs = highlighted?.Spike?.Ms, baselineMs = highlighted?.BaselineMs,
                maxTraceDelayMs = samples.Max(sample => sample.TraceDelayMs),
                eventsLost = samples.Max(sample => sample.EventsLost),
                states = samples.GroupBy(sample => sample.Status).ToDictionary(group => group.Key, group => group.Count())
            }));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            return 1;
        }
        finally
        {
            if (echo is not null)
            {
                using (echo)
                {
                    if (!echo.HasExited)
                    {
                        echo.Kill();
                        await echo.WaitForExitAsync();
                    }
                }
            }
        }
    }

    public static async Task<int> EchoAsync(string readyPipe, bool ipv6, string control)
    {
        if (!readyPipe.StartsWith("GameDesk-Test-", StringComparison.Ordinal) || !int.TryParse(control, out int parentPid))
            return 1;
        using var parent = Process.GetProcessById(parentPid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => timeout.Cancel();
        using var socket = new UdpClient(new IPEndPoint(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0));
        using (var ready = new NamedPipeClientStream(".", readyPipe, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await ready.ConnectAsync(5_000, timeout.Token);
            await using var writer = new StreamWriter(ready);
            await writer.WriteLineAsync(((IPEndPoint)socket.Client.LocalEndPoint!).Port.ToString());
        }
        try
        {
            while (!parent.HasExited)
            {
                var request = await socket.ReceiveAsync(timeout.Token);
                if (request.Buffer is [1])
                    await socket.SendAsync(request.Buffer, request.RemoteEndPoint, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        return 0;
    }

    public static int CheckReview(string folder, string report, string image)
    {
        try
        {
            CheckReviewReader();
            var files = SessionReview.List(folder);
            var file = files.FirstOrDefault() ??
                throw new InvalidOperationException("No session fixture was supplied.");
            var session = SessionReview.Load(file.Path, CancellationToken.None);
            using var window = new ControlWindow(() => { }, () => { }, () => { }, () => { },
                true, "Ctrl+Alt+F7", true, folder) { ShowInTaskbar = false, Opacity = 0 };
            window.ShowHistory();
            window.Show();
            var stopwatch = Stopwatch.StartNew();
            Label[] labels = Descendants(window).OfType<Label>().ToArray();
            while (!labels.Any(label => label.Text.Contains(" monitored")) && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
            bool loaded = labels.Any(label => label.Text.Contains(" monitored"));
            var incidents = Descendants(window).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Session incident");
            if (incidents.Items.Count > 0)
                incidents.SelectedIndex = 0;
            Application.DoEvents();
            var plot = Descendants(window).OfType<ReviewChart>().Single();
            bool layoutFits = Descendants(window).Where(control => control.Visible)
                .All(control => control.Parent!.ClientRectangle.Contains(control.Bounds));
            bool detail = incidents.Items.Count == 0 || labels.Any(label => label.Text.Contains("±5 s"));
            bool comparisonTested = files.Length > 1;
            bool comparisonLoaded = false;
            bool comparisonZoom = false;
            double? sharedSeconds = null;
            if (comparisonTested)
            {
                var selector = Descendants(window).OfType<ComboBox>().Single(combo => combo.AccessibleName == "Compare session");
                var zoom = Descendants(window).OfType<CheckBox>().Single(box => box.Text.StartsWith("Around selection", StringComparison.Ordinal));
                selector.SelectedIndex = 1;
                stopwatch.Restart();
                while (!labels.Any(label => label.Visible && label.Text.StartsWith("UDP gap p95 ", StringComparison.Ordinal)) &&
                    stopwatch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Application.DoEvents();
                    Thread.Sleep(15);
                }
                zoom.Checked = false;
                Application.DoEvents();
                var other = SessionReview.Load(files[1].Path, CancellationToken.None);
                sharedSeconds = Math.Min(other.Duration, session.Duration);
                comparisonLoaded = labels.Any(label => label.Visible && label.Text.Contains($"0:00–{SessionReview.Time(sharedSeconds.Value)}")) &&
                    labels.Any(label => label.Visible && label.Text.Contains("Match RTT p95"));
                layoutFits &= Descendants(window).Where(control => control.Visible)
                    .All(control => control.Parent!.ClientRectangle.Contains(control.Bounds));
                if (incidents.Items.Count > 0)
                {
                    incidents.SelectedIndex = 0;
                    Application.DoEvents();
                    comparisonZoom = labels.Any(label => label.Text.Contains("±5 s") && label.Text.Contains(" / "));
                }
            }
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size));
            bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            var clipped = labels.Where(label => label.Visible && label.Text.Length > 0 &&
                label.Height < label.GetPreferredSize(new Size(label.Width, 0)).Height)
                .Select(label => new { label.Text, label.Width, label.Height }).ToArray();
            bool passed = loaded && detail && layoutFits && clipped.Length == 0 && plot.Height > 150 && session.Udp.Length > 0 &&
                (!comparisonTested || comparisonLoaded);
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed, loaded, layoutFits, detail, dpi = window.DeviceDpi,
                seconds = session.Duration, udpPoints = session.Udp.Length, matchProbes = session.Match.Length,
                gatewayProbes = session.Gateway.Length, incidentCount = session.Incidents.Length, session.SkippedRecords,
                comparisonTested, comparisonLoaded, comparisonZoom, sharedSeconds, clipped }));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            return 1;
        }

        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child))
                    yield return nested;
            }
        }
    }

    private static void CheckReviewReader()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gamedesk-review-{Guid.NewGuid():N}.jsonl");
        try
        {
            var window = new NetworkSample(1500, "spike", BaselineMs: 18, Spike: new(1450, 93),
                Probes: new(new(1200, 30, "Success"), new(1200, 3, "Success")));
            string[] lines =
            [
                JsonSerializer.Serialize(new { type = "start", qpc = 1000, frequency = 100, utc = DateTimeOffset.UtcNow }),
                JsonSerializer.Serialize(new { type = "udp-gap", Qpc = 950, ms = 1000 }),
                JsonSerializer.Serialize(new { type = "udp-gap", Qpc = 1450, ms = 93 }),
                JsonSerializer.Serialize(new { type = "sample", value = window }),
                JsonSerializer.Serialize(new { type = "sample", value = window with { Qpc = 1600 } }),
                JsonSerializer.Serialize(new { type = "sample", value = new NetworkSample(1650, "spike",
                    Probes: new(new(1600, null, "TimedOut"), null)) }),
                JsonSerializer.Serialize(new { type = "felt-lag", qpc = 1660 }),
                "{"
            ];
            File.WriteAllLines(path, lines);
            var result = SessionReview.Load(path, CancellationToken.None);
            if (result.Duration != 6.6 || result.Udp is not [{ Seconds: 4.5, Ms: 93 }] ||
                result.Match is not [{ Seconds: 2, Ms: 30 }] || result.Gateway.Length != 1 ||
                result.Incidents is not [{ GapMs: 93 }, { FeltLag: true }] ||
                result.TypicalGap != 18 || result.SkippedRecords != 1)
                throw new InvalidDataException("Session review must preserve timestamps, deduplicate probes, and ignore partial lines.");
            var shared = ReviewRange.For(90, 43, 39, true);
            var sharedPoints = shared.Points([new(28, 500), new(29, 42), new(43, 44), new(44, 900)]).ToArray();
            if (shared != new ReviewRange(29, 43) || sharedPoints is not [{ Ms: 42 }, { Ms: 44 }] ||
                ReviewRange.For(90, 43, null, false) != new ReviewRange(0, 43))
                throw new InvalidDataException("Session comparisons must use the same bounded elapsed-time interval.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static int CheckPanel(string report, string image)
    {
        try
        {
            using var panel = new ControlWindow(() => { }, () => { }, () => { }, () => { }, true, "Ctrl+Alt+F7", true)
                { ShowInTaskbar = false, Opacity = 0 };
            panel.UpdateStatus("Windows missed network trace events. Waiting for fresh data.",
                "Stop network monitor", true, true, true, false);
            panel.Show();
            Application.DoEvents();
            var issues = FindClipping(panel).ToList();
            long now = Stopwatch.GetTimestamp();
            var points = Enumerable.Range(0, 60).Select(i => new GapPoint(now - (59 - i) * Stopwatch.Frequency / 4,
                i == 36 ? 93 : 18 + 3 * Math.Sin(i * 0.42))).ToArray();
            var sample = new NetworkSample(now, "stable", BaselineMs: 20, Points: points,
                Probes: new(new(now, 43, "Success"), new(now, 2, "Success")));
            panel.UpdateStatus("Match connection detected. UDP arrivals are steady.",
                "Stop network monitor", true, true, false, true, sample, "Google Cloud · Hong Kong");
            Application.DoEvents();
            issues.AddRange(FindClipping(panel));
            using var bitmap = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size));
            bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            bool passed = issues.Count == 0;
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed, dpi = panel.DeviceDpi,
                clientWidth = panel.ClientSize.Width, clientHeight = panel.ClientSize.Height, issues }));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            return 1;
        }

        static IEnumerable<string> FindClipping(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible)
                    continue;
                bool clipped = !parent.ClientRectangle.Contains(child.Bounds);
                if (child is Label or Button or CheckBox)
                {
                    var preferred = child.GetPreferredSize(child.Size);
                    clipped |= child.Width < preferred.Width || child.Height < preferred.Height;
                }
                if (clipped)
                    yield return $"{child.GetType().Name}: {child.Text} (bounds {child.Bounds}, parent {parent.ClientSize})";
                foreach (var issue in FindClipping(child))
                    yield return issue;
            }
        }
    }

    public static int CheckOverlay(string report, string image)
    {
        try
        {
            var providers = ProviderCatalog.Load();
            bool providerLabels = ProviderLabelsAreCorrect(providers);
            using var overlay = new Overlay { Opacity = 0 };
            var now = Stopwatch.GetTimestamp();
            var points = Enumerable.Range(0, 60).Select(i => new GapPoint(now - (59 - i) * Stopwatch.Frequency / 4,
                i == 50 ? 118 : 18 + i * 13 % 8)).ToArray();
            string endpoint = "35.220.27.0:5735";
            string provider = providers.Resolve(endpoint);
            for (int index = 6; index >= 0; index--)
            {
                long time = now - index * 2 * Stopwatch.Frequency;
                var match = index == 2 ? new PingResult(time, null, "TimedOut")
                    : new PingResult(time, index == 0 ? 76 : 31 + index, "Success");
                overlay.UpdateReading(new(time, "spike", Endpoint: endpoint, BaselineMs: 21,
                    Spike: points[50], Points: points, Probes: new(match, new(time, 2 + index % 3, "Success"))), 0, provider);
            }
            overlay.MoveTo(Screen.PrimaryScreen!.Bounds);
            overlay.Show();
            Application.DoEvents();
            overlay.MoveTo(Screen.PrimaryScreen.Bounds);
            Application.DoEvents();
            using var bitmap = new Bitmap(overlay.ClientSize.Width, overlay.ClientSize.Height);
            overlay.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size));
            bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            float scale = overlay.DeviceDpi / 96f;
            bool painted = bitmap.GetPixel((int)Math.Round(11 * scale), (int)Math.Round(17 * scale)).R > 200;
            overlay.Detailed = true;
            var screen = Screen.PrimaryScreen.Bounds;
            overlay.MoveTo(screen);
            Application.DoEvents();
            using var detailed = new Bitmap(overlay.ClientSize.Width, overlay.ClientSize.Height);
            overlay.DrawToBitmap(detailed, new(Point.Empty, detailed.Size));
            detailed.Save(Path.ChangeExtension(image, ".detailed.png"), System.Drawing.Imaging.ImageFormat.Png);
            bool horizontal = detailed.Width > bitmap.Width && detailed.Height == bitmap.Height &&
                detailed.Height <= Math.Round(35 * scale);
            bool flush = overlay.Bounds.Top == screen.Top && overlay.Bounds.Right == screen.Right;
            var narrowArea = new Rectangle(screen.Location, new Size(Math.Min(420, screen.Width), Math.Min(240, screen.Height)));
            overlay.MoveTo(narrowArea);
            bool narrowFits = narrowArea.Contains(overlay.Bounds);
            bool hotkeys = overlay.ToggleRegistered && overlay.MarkerRegistered && overlay.DetailShortcut is not null;
            PingResult[] probes = [new(1, 30, "Success"), new(2, 31, "Success"), new(3, 32, "Success"),
                new(4, 76, "Success")];
            bool probeAlerts = Overlay.ProbeIsHigh(76, probes, 4, 15) && !Overlay.ProbeIsHigh(34, probes, 4, 15) &&
                !Overlay.ProbeIsHigh(76, probes, 3, 15);
            bool passed = painted && horizontal && flush && narrowFits && providerLabels && probeAlerts;
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed, painted, horizontal, flush, narrowFits, providerLabels,
                probeAlerts, hotkeys, compactWidth = bitmap.Width, detailedWidth = detailed.Width, bitmap.Height }));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            return 1;
        }
    }

    private static bool ProviderLabelsAreCorrect(ProviderCatalog providers) =>
        providers.Resolve("15.230.4.164:5735") == "AWS · Jakarta" &&
        providers.Resolve("34.101.18.0:5735") == "Google Cloud · Jakarta" &&
        providers.Resolve("35.220.27.0:5735") == "Google Cloud · Hong Kong" &&
        providers.Resolve("[::ffff:34.101.18.0]:5735") == "Google Cloud · Jakarta" &&
        providers.Resolve("120.52.22.96:5735") == "AWS · Global edge" &&
        providers.Resolve("192.168.1.1:5735") == "Local / private" &&
        providers.Resolve("203.0.113.7:5735") == "Unknown provider";
}
