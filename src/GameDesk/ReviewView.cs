namespace GameDesk;

internal sealed class ReviewView : UserControl
{
    private readonly string folder;
    private readonly ComboBox sessions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Recorded session" };
    private readonly ComboBox compareWith = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Compare session" };
    private readonly ComboBox incidents = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Session incident" };
    private readonly CheckBox zoom = new() { Text = "Around selection (±10 s)", AutoSize = true };
    private readonly Label summary = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label comparisonStats = new() { AutoSize = true, Dock = DockStyle.Fill, Visible = false };
    private readonly Label detail = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly ReviewChart chart = new() { Dock = DockStyle.Fill };
    private CancellationTokenSource? loading;
    private SessionReview? current;
    private SessionReview? reference;
    private double? selectedTime;
    private bool updatingFiles;

    public ReviewView(string folder)
    {
        this.folder = folder;
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        var refresh = new Button { Text = "Refresh", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        refresh.Click += async (_, _) => await Reload();
        sessions.SelectedIndexChanged += async (_, _) =>
        {
            if (!updatingFiles)
            {
                UpdateComparisonChoices((compareWith.SelectedItem as ReviewFile)?.Path);
                await LoadSelected();
            }
        };
        compareWith.SelectedIndexChanged += async (_, _) =>
        {
            if (!updatingFiles)
                await LoadSelected();
        };
        incidents.SelectedIndexChanged += (_, _) =>
        {
            if (incidents.SelectedItem is not ReviewIncident incident)
                return;
            selectedTime = incident.Seconds;
            zoom.Checked = true;
            UpdateReading();
        };
        zoom.CheckedChanged += (_, _) => UpdateReading();
        chart.TimeSelected += time =>
        {
            incidents.SelectedIndex = -1;
            selectedTime = time;
            UpdateReading();
        };
        Load += async (_, _) => await Reload();
        Resize += (_, _) => FitLabels();
        summary.TextChanged += (_, _) => FitLabel(summary);
        comparisonStats.TextChanged += (_, _) => FitLabel(comparisonStats);
        detail.TextChanged += (_, _) => FitLabel(detail);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Toolbar("Session A", sessions, refresh), 0, 0);
        layout.Controls.Add(Toolbar("Compare with", compareWith), 0, 1);
        layout.Controls.Add(summary, 0, 2);
        layout.Controls.Add(comparisonStats, 0, 3);
        layout.Controls.Add(Toolbar("Jump to", incidents, zoom), 0, 4);
        layout.Controls.Add(chart, 0, 5);
        layout.Controls.Add(detail, 0, 6);
        Controls.Add(layout);
        FitLabels();
        UpdateReading();
    }

    private static TableLayoutPanel Toolbar(string name, Control center, Control? right = null)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(center, 1, 0);
        if (right is not null)
            row.Controls.Add(right, 2, 0);
        return row;
    }

    private void FitLabels()
    {
        FitLabel(summary);
        FitLabel(comparisonStats);
        FitLabel(detail);
    }

    private void FitLabel(Label label)
    {
        int width = Math.Max(80, ClientSize.Width - 42);
        label.MaximumSize = new Size(width, 0);
        int height = label.Text.Length == 0 ? 0 : TextRenderer.MeasureText(label.Text, label.Font,
            new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;
        label.MinimumSize = new Size(0, height);
    }

    private async Task Reload()
    {
        var prior = (sessions.SelectedItem as ReviewFile)?.Path;
        var previousReference = (compareWith.SelectedItem as ReviewFile)?.Path;
        try
        {
            var files = SessionReview.List(folder);
            updatingFiles = true;
            try
            {
                sessions.Items.Clear();
                sessions.Items.AddRange(files);
                if (files.Length > 0)
                    sessions.SelectedIndex = Math.Max(0, Array.FindIndex(files, file => file.Path == prior));
                UpdateComparisonChoices(previousReference);
            }
            finally { updatingFiles = false; }
            if (files.Length == 0)
                Reset("No recorded sessions yet. Start the network monitor and play a match.");
            else
                await LoadSelected();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Reset($"Unable to list the session logs. {error.Message}");
        }
    }

    private void UpdateComparisonChoices(string? prior)
    {
        bool wasUpdating = updatingFiles;
        updatingFiles = true;
        try
        {
            var first = sessions.SelectedItem as ReviewFile;
            var choices = sessions.Items.OfType<ReviewFile>().Where(file => file.Path != first?.Path).ToArray();
            compareWith.Items.Clear();
            compareWith.Items.Add("None");
            compareWith.Items.AddRange(choices);
            compareWith.SelectedIndex = Array.FindIndex(choices, file => file.Path == prior) + 1;
            compareWith.Enabled = first is not null && choices.Length > 0;
        }
        finally { updatingFiles = wasUpdating; }
    }

    private async Task LoadSelected()
    {
        if (sessions.SelectedItem is not ReviewFile file)
            return;
        var comparison = compareWith.SelectedItem as ReviewFile;
        Reset(comparison is null ? "Loading session…" : "Loading comparison…");
        using var cancellation = new CancellationTokenSource();
        loading = cancellation;
        try
        {
            var result = await Task.Run(() => (
                Primary: SessionReview.Load(file.Path, cancellation.Token),
                Reference: comparison is null ? null : SessionReview.Load(comparison.Path, cancellation.Token)), cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed)
                return;
            current = result.Primary;
            reference = result.Reference;
            double duration = Math.Min(current.Duration, reference?.Duration ?? current.Duration);
            incidents.Items.AddRange(current.Incidents.Where(incident => incident.Seconds <= duration).ToArray());
            UpdateReading();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!cancellation.IsCancellationRequested)
                Reset($"Unable to read the selected sessions. {error.Message}");
        }
        finally
        {
            if (loading == cancellation)
                loading = null;
        }
    }

    private void Reset(string message)
    {
        loading?.Cancel();
        current = reference = null;
        selectedTime = null;
        incidents.Items.Clear();
        zoom.Checked = false;
        summary.Text = message;
        UpdateReading();
    }

    private void UpdateReading()
    {
        incidents.Enabled = incidents.Items.Count > 0;
        zoom.Enabled = selectedTime.HasValue;
        comparisonStats.Visible = reference is not null;
        if (current is not { } a)
        {
            chart.UpdateChart(null, null, null, default);
            detail.Text = "";
            return;
        }

        var visible = ReviewRange.For(a.Duration, reference?.Duration, selectedTime, zoom.Checked);
        chart.UpdateChart(a, reference, selectedTime, visible);
        if (reference is { } b)
        {
            summary.Text = $"Shared elapsed time {SessionReview.Time(visible.Start)}–{SessionReview.Time(visible.End)}" +
                "    ·    A solid blue    ·    B dashed orange";
            comparisonStats.Text = visible.End > visible.Start
                ? CompareMetrics(a, b, visible, false) + DataNote(a, "A") + DataNote(b, "B")
                : "No shared time range.";
        }
        else
        {
            summary.Text = SingleSummary(a);
        }
        if (selectedTime is not double position)
        {
            detail.Text = incidents.Enabled
                ? "Choose an incident or click the timeline to inspect nearby samples." +
                    (reference is null ? " Dotted lines mark felt lag." : " Dotted lines mark felt lag in A.")
                : "Click the timeline to inspect nearby samples.";
            return;
        }

        double duration = Math.Min(a.Duration, reference?.Duration ?? a.Duration);
        var nearby = new ReviewRange(Math.Max(0, position - 5), Math.Min(duration, position + 5));
        detail.Text = reference is { } other
            ? $"{SessionReview.Time(position)} ±5 s    ·    {CompareMetrics(a, other, nearby, true)}"
            : $"{SessionReview.Time(position)} ±5 s    ·    UDP peak {SessionReview.Ms(Peak(nearby, a.Udp))}" +
              $"    ·    Match RTT median {SessionReview.Ms(Percentile(nearby, a.Match, 0.5))}" +
              $"    ·    Gateway RTT median {SessionReview.Ms(Percentile(nearby, a.Gateway, 0.5))}";
    }

    private static string SingleSummary(SessionReview a)
    {
        var full = new ReviewRange(0, a.Duration);
        return $"{SessionReview.Time(a.Duration)} monitored    ·    Typical UDP gap {SessionReview.Ms(a.TypicalGap)}" +
            $"    ·    Peak UDP gap {SessionReview.Ms(Peak(full, a.Udp))}" +
            $"    ·    Match RTT p95 {SessionReview.Ms(Percentile(full, a.Match, 0.95))}" + DataNote(a);
    }

    private static string CompareMetrics(SessionReview a, SessionReview b, ReviewRange range, bool nearby)
    {
        double fraction = nearby ? 0.5 : 0.95;
        string rttLabel = nearby ? "median" : "p95";
        string udpLabel = nearby ? "UDP peak" : "UDP gap p95";
        double? udpA = nearby ? Peak(range, a.Udp) : Percentile(range, a.Udp, fraction);
        double? udpB = nearby ? Peak(range, b.Udp) : Percentile(range, b.Udp, fraction);
        return $"{udpLabel} {Pair(udpA, udpB)}" +
            $"    ·    Match RTT {rttLabel} {Pair(Percentile(range, a.Match, fraction), Percentile(range, b.Match, fraction))}" +
            $"    ·    Gateway RTT {rttLabel} {Pair(Percentile(range, a.Gateway, fraction), Percentile(range, b.Gateway, fraction))}";

        static string Pair(double? first, double? second) => $"{SessionReview.Ms(first)} / {SessionReview.Ms(second)}";
    }

    private static string DataNote(SessionReview session, string? label = null)
    {
        string prefix = label is null ? "" : label + " ";
        string missed = session.TraceEventsLost > 0 ? $"    ·    {prefix}{session.TraceEventsLost} trace events missed" : "";
        string unreadable = session.SkippedRecords > 0 ? $"    ·    {prefix}{session.SkippedRecords} unreadable records" : "";
        return missed + unreadable;
    }

    private static double? Peak(ReviewRange range, ReviewPoint[] points) =>
        range.Points(points).Select(point => (double?)point.Ms).Max();

    private static double? Percentile(ReviewRange range, ReviewPoint[] points, double fraction) =>
        SessionReview.Percentile(range.Points(points).Select(point => point.Ms), fraction);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            loading?.Cancel();
        base.Dispose(disposing);
    }
}
