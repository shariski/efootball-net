using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace GameDesk;

internal sealed class LiveGapChart : Control
{
    private NetworkSample? sample;

    public LiveGapChart()
    {
        BackColor = Color.White;
        ForeColor = Color.FromArgb(30, 45, 60);
        DoubleBuffered = true;
        ResizeRedraw = true;
        AccessibleName = "Incoming UDP packet arrival gaps in the last 15 seconds";
    }

    public void ShowSample(NetworkSample? reading)
    {
        if (ReferenceEquals(reading, sample))
            return;
        sample = reading;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float unit = DeviceDpi / 96f;
        float padding = 14 * unit;
        using var text = new SolidBrush(ForeColor);
        using var secondary = new SolidBrush(Color.FromArgb(89, 105, 122));
        g.DrawString("Incoming UDP gaps", Font, text, padding, padding);
        string caption = "Last 15 s";
        g.DrawString(caption, Font, secondary, Width - padding - g.MeasureString(caption, Font).Width, padding);

        var plot = RectangleF.FromLTRB(Font.Height * 3f, 43 * unit, Width - padding, Height - 30 * unit);
        if (plot.Width <= 0 || plot.Height <= 10)
            return;

        using var grid = new Pen(Color.FromArgb(218, 226, 235));
        g.DrawLine(grid, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        g.DrawLine(grid, plot.Left, plot.Top, plot.Right, plot.Top);
        if (sample is not { Points: { Length: > 0 } points } reading)
        {
            g.DrawString("No match samples yet", Font, secondary, plot.Left, plot.Top + plot.Height / 2);
            return;
        }

        double maximum = Math.Max(40, Math.Ceiling(points.Max(point => point.Ms) / 10) * 10);
        g.DrawString($"{maximum:0} ms", Font, secondary, 6 * unit, plot.Top - Font.Height / 2f);
        g.DrawString("0", Font, secondary, 15 * unit, plot.Bottom - Font.Height / 2f);
        g.DrawString("15 s ago", Font, secondary, plot.Left, plot.Bottom + 5 * unit);
        string now = "Now";
        g.DrawString(now, Font, secondary, plot.Right - g.MeasureString(now, Font).Width, plot.Bottom + 5 * unit);
        if (reading.BaselineMs is double baseline)
        {
            float y = Y(baseline);
            using var guide = new Pen(Color.FromArgb(170, 183, 197)) { DashStyle = DashStyle.Dash };
            g.DrawLine(guide, plot.Left, y, plot.Right, y);
        }

        var color = reading.Status == "spike" ? Color.FromArgb(184, 93, 16) : Color.FromArgb(21, 108, 164);
        using var line = new Pen(color, Math.Max(1, 1.25f * unit));
        using var fill = new SolidBrush(color);
        using var path = new GraphicsPath();
        const double history = 15;
        long second = Stopwatch.Frequency;
        PointF? previous = null;
        long previousQpc = 0;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var point in points)
        {
            double position = 1 - (reading.Qpc - point.Qpc) / (history * second);
            var next = new PointF(plot.Left + (float)Math.Clamp(position, 0, 1) * plot.Width, Y(point.Ms));
            if (previous is { } last && point.Qpc - previousQpc <= second / 2)
                path.AddLine(last, next);
            else
            {
                path.StartFigure();
                g.FillEllipse(fill, next.X - unit, next.Y - unit, unit * 2, unit * 2);
            }
            previous = next;
            previousQpc = point.Qpc;
        }
        if (path.PointCount > 0)
            g.DrawPath(line, path);

        float Y(double ms) => plot.Bottom - (float)Math.Clamp(ms / maximum, 0, 1) * plot.Height;
    }
}
