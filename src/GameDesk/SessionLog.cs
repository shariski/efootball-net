using System.Diagnostics;
using System.Text.Json;

namespace GameDesk;

internal sealed class SessionLog : IDisposable
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDesk", "sessions");
    private readonly StreamWriter writer;
    private long lastFlush;
    private long lastPoint;
    private string? endpoint;
    private string? localAddress;

    public SessionLog()
    {
        Directory.CreateDirectory(Folder);
        var filename = $"network-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl";
        var file = new FileStream(Path.Combine(Folder, filename), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer = new StreamWriter(file);
        long qpc = Stopwatch.GetTimestamp();
        Write(new { type = "start", version = 1, qpc, frequency = Stopwatch.Frequency, utc = DateTimeOffset.UtcNow });
    }

    public void Record(NetworkSample sample)
    {
        if (endpoint != sample.Endpoint || localAddress != sample.LocalAddress)
        {
            endpoint = sample.Endpoint;
            localAddress = sample.LocalAddress;
            lastPoint = 0;
        }
        // The rolling plot is sent for display. Log its new or updated bucket
        // peaks once so the complete session stays compact.
        foreach (var point in sample.Points ?? [])
        {
            if (point.Qpc > lastPoint)
            {
                Write(new { type = "udp-gap", endpoint, point.Qpc, ms = point.Ms });
                lastPoint = point.Qpc;
            }
        }
        Write(new { type = "sample", value = sample with { Points = null } });
        if (sample.Qpc - lastFlush >= Stopwatch.Frequency)
        {
            writer.Flush();
            lastFlush = sample.Qpc;
        }
    }

    public void Mark(long qpc)
    {
        Write(new { type = "felt-lag", qpc });
        writer.Flush();
    }

    private void Write<T>(T entry) => writer.WriteLine(JsonSerializer.Serialize(entry));

    public void Dispose() => writer.Dispose();
}
