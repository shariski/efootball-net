using System.Globalization;
using System.Text.Json;

namespace GameDesk;

internal record ReviewPoint(double Seconds, double Ms);

internal readonly record struct ReviewRange(double Start, double End)
{
    public static ReviewRange For(double primaryDuration, double? referenceDuration, double? selection, bool zoom)
    {
        double duration = Math.Min(primaryDuration, referenceDuration ?? primaryDuration);
        if (!zoom || selection is not double time)
            return new(0, duration);
        double center = Math.Clamp(time, 0, duration);
        return new(Math.Max(0, center - 10), Math.Min(duration, center + 10));
    }

    public IEnumerable<ReviewPoint> Points(IEnumerable<ReviewPoint> series)
    {
        double start = Start;
        double end = End;
        return series.Where(point => point.Seconds >= start && point.Seconds <= end);
    }
}
internal record ReviewIncident(double Seconds, double? GapMs, bool Largest = false)
{
    public bool FeltLag => GapMs is null;
    public override string ToString()
    {
        string time = SessionReview.Time(Seconds);
        if (FeltLag)
            return $"{time}   Felt lag";
        return Largest ? $"{time}   Largest UDP gap {GapMs:0.#} ms" : $"{time}   UDP spike {GapMs:0.#} ms";
    }
}

internal sealed record ReviewFile(string Path, DateTimeOffset Started)
{
    public override string ToString() => Started.ToLocalTime().ToString("ddd, dd MMM yyyy HH:mm:ss");
}

internal sealed record SessionReview(double Duration, ReviewPoint[] Udp,
    ReviewPoint[] Match, ReviewPoint[] Gateway, ReviewIncident[] Incidents, double? TypicalGap,
    int TraceEventsLost, int SkippedRecords)
{
    public static ReviewFile[] List(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        var files = new List<ReviewFile>();
        foreach (string file in Directory.EnumerateFiles(folder, "network-*.jsonl"))
        {
            string name = Path.GetFileName(file);
            if (name.Length > 23 && DateTimeOffset.TryParseExact(name.AsSpan(8, 15), "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                files.Add(new(file, date));
        }
        return files.OrderByDescending(file => file.Started).ToArray();
    }

    public static SessionReview Load(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        long origin = 0, frequency = 0, end = 0;
        var gaps = new SortedDictionary<long, double>();
        var match = new SortedDictionary<long, double>();
        var gateway = new SortedDictionary<long, double>();
        var spikes = new SortedDictionary<long, double>();
        var feltLag = new SortedSet<long>();
        var typical = new List<double>();
        int lostMin = int.MaxValue, lostMax = 0, skipped = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(line);
                var row = doc.RootElement;
                string? type = row.GetProperty("type").GetString();
                if (type == "start")
                {
                    if (frequency > 0)
                        continue;
                    long candidateOrigin = row.GetProperty("qpc").GetInt64();
                    long candidateFrequency = row.GetProperty("frequency").GetInt64();
                    if (candidateOrigin < 0 || candidateFrequency <= 0)
                        continue;
                    origin = end = candidateOrigin;
                    frequency = candidateFrequency;
                    continue;
                }
                if (frequency == 0)
                    continue;

                if (type == "sample")
                {
                    var sample = row.GetProperty("value").Deserialize<NetworkSample>();
                    if (sample is null || sample.Qpc < origin)
                        continue;
                    end = Math.Max(end, sample.Qpc);
                    lostMin = Math.Min(lostMin, sample.EventsLost);
                    lostMax = Math.Max(lostMax, sample.EventsLost);
                    if (sample.BaselineMs is double baseline && double.IsFinite(baseline) && baseline >= 0)
                        typical.Add(baseline);
                    if (sample.Spike is { } spike && spike.Qpc >= origin && double.IsFinite(spike.Ms) && spike.Ms >= 0)
                        spikes[spike.Qpc] = spike.Ms;
                    AddProbe(match, sample.Probes?.Match, origin);
                    AddProbe(gateway, sample.Probes?.Gateway, origin);
                }
                else if (type == "udp-gap")
                {
                    long qpc = row.GetProperty("Qpc").GetInt64();
                    double gap = row.GetProperty("ms").GetDouble();
                    if (qpc >= origin && double.IsFinite(gap) && gap >= 0)
                    {
                        gaps[qpc] = gap;
                        end = Math.Max(end, qpc);
                    }
                }
                else if (type == "felt-lag")
                {
                    long qpc = row.GetProperty("qpc").GetInt64();
                    if (qpc >= origin)
                    {
                        feltLag.Add(qpc);
                        end = Math.Max(end, qpc);
                    }
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Capture may still be writing the last line. Earlier records remain usable.
                skipped++;
            }
        }
        if (frequency == 0)
            throw new InvalidDataException("This log has no readable session header.");

        double Elapsed(long qpc) => (qpc - origin) / (double)frequency;
        ReviewPoint[] Points(SortedDictionary<long, double> values) =>
            values.Select(pair => new ReviewPoint(Elapsed(pair.Key), pair.Value)).ToArray();
        var incidents = feltLag.Select(qpc => new ReviewIncident(Elapsed(qpc), null))
            .Concat(spikes.Select(spike => new ReviewIncident(Elapsed(spike.Key), spike.Value))).ToList();
        if (gaps.Count > 0)
        {
            var peak = gaps.MaxBy(gap => gap.Value);
            if (!spikes.ContainsKey(peak.Key))
                incidents.Add(new(Elapsed(peak.Key), peak.Value, Largest: true));
        }
        return new(Elapsed(end), Points(gaps), Points(match), Points(gateway), incidents.OrderBy(incident => incident.Seconds).ToArray(),
            Percentile(typical, 0.5), lostMin == int.MaxValue ? 0 : Math.Max(0, lostMax - lostMin), skipped);
    }

    private static void AddProbe(SortedDictionary<long, double> points, PingResult? probe, long origin)
    {
        if (probe is { Ms: >= 0, Status: "Success" } && probe.Qpc >= origin)
            points[probe.Qpc] = probe.Ms.Value;
    }

    public static double? Percentile(IEnumerable<double> observations, double fraction)
    {
        var ordered = observations.Order().ToArray();
        if (ordered.Length == 0)
            return null;
        double position = fraction * (ordered.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    public static string Ms(double? value) => value is double ms ? $"{ms:0.#} ms" : "—";

    public static string Time(double seconds)
    {
        double whole = Math.Max(0, Math.Floor(seconds));
        return $"{Math.Floor(whole / 60):0}:{whole % 60:00}";
    }
}
