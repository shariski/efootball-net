using System.Drawing.Drawing2D;

namespace GameDesk;

internal sealed class ReviewChart : Control
{
    private const double UdpContinuity = 1;
    private const double RttContinuity = 3.5;
    private static readonly Color PrimaryColor = Color.FromArgb(27, 115, 172);
    private static readonly Color ReferenceColor = Color.FromArgb(184, 93, 16);
    private static readonly Color MatchColor = Color.FromArgb(23, 125, 89);
    private static readonly Color GatewayColor = Color.FromArgb(170, 97, 18);
    private SessionReview? current;
    private SessionReview? reference;
    private double? selected;
    private ReviewRange range;
    private RectangleF timeline;

    private record Series(ReviewPoint[] Points, Color Color, double Continuity, bool Dashed = false);
    private record Label(string Text, Color Color);
    private record Plot(Label[] Labels, Series[] Series);

    public event Action<double>? TimeSelected;

    public ReviewChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        TabStop = true;
        AccessibleName = "Session charts. Use left and right arrow keys to select time.";
    }

    public void UpdateChart(SessionReview? session, SessionReview? comparison, double? position, ReviewRange bounds)
    {
        current = session;
        reference = comparison;
        selected = position;
        range = bounds;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        timeline = RectangleF.Empty;
        if (current is not { } session || ClientSize.Width < Font.Height * 12)
            return;

        Plot[] charts = reference is { } other
            ? [Pair("Incoming UDP gap peaks (ms)", session.Udp, other.Udp, UdpContinuity),
               Pair("Match RTT (ms)", session.Match, other.Match, RttContinuity),
               Pair("Gateway RTT (ms)", session.Gateway, other.Gateway, RttContinuity)]
            : [new([new("Incoming UDP gap peaks (ms)", ForeColor)], [new(session.Udp, PrimaryColor, UdpContinuity)]),
               new([new("Match RTT", MatchColor), new("Gateway RTT (ms)", GatewayColor)],
                   [new(session.Match, MatchColor, RttContinuity), new(session.Gateway, GatewayColor, RttContinuity)])];

        float labelHeight = Font.Height;
        float labelSpace = labelHeight * 1.7f;
        float plotHeight = (Height - charts.Length * labelSpace - labelHeight * 1.65f) / charts.Length;
        if (plotHeight < labelHeight)
            return;

        float left = MathF.Max(34, labelHeight * 2.8f);
        float right = Width - labelHeight * 0.7f;
        float bottom = 0;
        float plotWidth = right - left;
        using var gray = new SolidBrush(SystemColors.GrayText);
        using var grid = new Pen(SystemColors.ControlDark);
        double span = Math.Max(0.1, range.End - range.Start);

        for (int i = 0; i < charts.Length; i++)
        {
            float top = labelSpace + i * (labelSpace + plotHeight);
            var bounds = new RectangleF(left, top, plotWidth, plotHeight);
            bottom = bounds.Bottom;
            var chart = charts[i];
            float labelX = left;
            foreach (var label in chart.Labels)
            {
                using var brush = new SolidBrush(label.Color);
                g.DrawString(label.Text, Font, brush, labelX, top - labelHeight * 1.35f);
                labelX += g.MeasureString(label.Text, Font).Width + labelHeight;
            }
            DrawPlot(bounds, chart.Series);
        }

        timeline = RectangleF.FromLTRB(left, labelSpace, right, bottom);
        for (int i = 0; i <= 4; i++)
        {
            float x = left + plotWidth * i / 4;
            string label = SessionReview.Time(range.Start + (range.End - range.Start) * i / 4);
            float labelWidth = g.MeasureString(label, Font).Width;
            g.DrawString(label, Font, gray, Math.Clamp(x - labelWidth / 2, 0, Math.Max(0, Width - labelWidth)), bottom + 3);
        }

        Plot Pair(string title, ReviewPoint[] a, ReviewPoint[] b, double continuity) =>
            new([new(title, ForeColor)], [new(a, PrimaryColor, continuity), new(b, ReferenceColor, continuity, true)]);

        void DrawPlot(RectangleF bounds, Series[] series)
        {
            var visible = series.Select(line => line with { Points = range.Points(line.Points).ToArray() }).ToArray();
            double high = visible.SelectMany(line => line.Points).Select(point => point.Ms).DefaultIfEmpty().Max();
            double max = Math.Max(10, Math.Ceiling(high / 10) * 10);
            g.DrawLine(grid, bounds.Left, bounds.Top, bounds.Left, bounds.Bottom);
            g.DrawLine(grid, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
            g.DrawLine(grid, bounds.Left, bounds.Top, bounds.Right, bounds.Top);
            g.DrawString($"{max:0}", Font, gray, 0, bounds.Top - labelHeight / 2);
            g.DrawString("0", Font, gray, 0, bounds.Bottom - labelHeight / 2);
            if (visible.All(line => line.Points.Length == 0))
                g.DrawString("No samples in this interval", Font, gray, bounds.Left + 8, bounds.Top + bounds.Height / 2 - labelHeight / 2);

            using var marker = new Pen(SystemColors.ControlDark) { DashStyle = DashStyle.Dot };
            foreach (var incident in session.Incidents.Where(item => item.FeltLag && item.Seconds >= range.Start && item.Seconds <= range.End))
            {
                float x = X(incident.Seconds);
                g.DrawLine(marker, x, bounds.Top, x, bounds.Bottom);
            }

            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var line in visible)
            {
                using var pen = new Pen(line.Color, Math.Max(1, DeviceDpi / 96f))
                    { DashStyle = line.Dashed ? DashStyle.Dash : DashStyle.Solid };
                using var fill = new SolidBrush(line.Color);
                using var path = new GraphicsPath();
                PointF? previous = null;
                double previousTime = 0;
                foreach (var reading in line.Points)
                {
                    var pixel = new PointF(X(reading.Seconds), bounds.Bottom - (float)(reading.Ms / max) * bounds.Height);
                    if (previous is { } last && reading.Seconds - previousTime <= line.Continuity)
                        path.AddLine(last, pixel);
                    else
                    {
                        path.StartFigure();
                        g.FillEllipse(fill, pixel.X - 1.5f, pixel.Y - 1.5f, 3, 3);
                    }
                    previous = pixel;
                    previousTime = reading.Seconds;
                }
                if (path.PointCount > 0)
                    g.DrawPath(pen, path);
            }
            g.SmoothingMode = old;
            if (selected is double at && at >= range.Start && at <= range.End)
            {
                using var cursor = new Pen(SystemColors.Highlight, Math.Max(1, DeviceDpi / 96f));
                float x = X(at);
                g.DrawLine(cursor, x, bounds.Top, x, bounds.Bottom);
            }

            float X(double time) => bounds.Left + (float)((time - range.Start) / span) * bounds.Width;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && timeline.Contains(e.Location) && current is not null)
        {
            Focus();
            TimeSelected?.Invoke(range.Start + (e.X - timeline.Left) / timeline.Width * (range.End - range.Start));
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (current is { } session && e.KeyCode is Keys.Left or Keys.Right)
        {
            double limit = Math.Min(session.Duration, reference?.Duration ?? session.Duration);
            double step = e.KeyCode == Keys.Left ? -1 : 1;
            TimeSelected?.Invoke(Math.Clamp((selected ?? range.Start) + step, 0, limit));
            e.Handled = true;
        }
    }
}
