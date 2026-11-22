using System.Diagnostics;
using System.Net;

namespace GameDesk;

internal record GapPoint(long Qpc, double Ms);

internal record NetworkSample(long Qpc, string Status, string? Endpoint = null, string? LocalAddress = null,
    double? BaselineMs = null, GapPoint? Spike = null, GapPoint[]? Points = null,
    int EventsLost = 0, long TraceDelayMs = 0, string? Error = null, ProbePair? Probes = null);

internal sealed class NetworkMonitor
{
    private static readonly long Second = Stopwatch.Frequency;
    private static readonly long Bucket = Math.Max(1, Second / 4);
    private readonly Dictionary<FlowKey, Flow> flows = [];
    private readonly object gate = new();
    private Flow? selected;
    private int target;
    private int eventsLost;
    private int observationLoss;

    public int Target => Volatile.Read(ref target);

    public void SetTarget(int pid)
    {
        lock (gate)
        {
            if (pid == target)
                return;
            Volatile.Write(ref target, pid);
            Clear();
            eventsLost = observationLoss = 0;
        }
    }

    public void Observe(int pid, long qpc, bool incoming, IPAddress local, int localPort,
        IPAddress remote, int remotePort)
    {
        if (pid == 0 || remotePort is 0 or 53 or 123 or 3478 or 5349 ||
            remote.IsIPv6Multicast || remote.Equals(IPAddress.Broadcast))
            return;
        lock (gate)
        {
            if (target != pid)
                return;
            var key = new FlowKey(local, localPort, remote, remotePort);
            if (!flows.TryGetValue(key, out var flow))
            {
                if (flows.Count >= 128)
                {
                    var oldest = flows.Values.Where(item => item != selected).MinBy(item => item.LastSeen);
                    if (oldest is not null)
                        flows.Remove(oldest.Key);
                }
                flows.Add(key, flow = new Flow(key, qpc));
            }
            flow.Observe(qpc, incoming, Stopwatch.GetTimestamp());
        }
    }

    public NetworkSample Sample(long now, int lost)
    {
        lock (gate)
        {
            if (lost != observationLoss)
            {
                Clear();
                observationLoss = lost;
                eventsLost = lost;
                return new(now, "trace-loss", EventsLost: lost);
            }

            var expired = flows.Values.Where(flow => now - flow.LastSeen > 12 * Second).ToArray();
            foreach (var flow in expired)
                flows.Remove(flow.Key);

            if (selected is { } current && now - Math.Min(current.LastIncoming, current.LastOutgoing) > 8 * Second)
                selected = null;
            selected ??= flows.Values
                .Where(flow => flow.Eligible(now))
                .MaxBy(flow => flow.BidirectionalRate(now));

            return selected is { } match ? match.Sample(now, eventsLost)
                : new(now, target == 0 ? "waiting-game" : "waiting-traffic", EventsLost: eventsLost);
        }
    }

    private void Clear()
    {
        flows.Clear();
        selected = null;
    }

    private record struct FlowKey(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort)
    {
        public string Endpoint => new IPEndPoint(Remote, RemotePort).ToString();
    }

    private sealed class Flow(FlowKey key, long firstSeen)
    {
        private readonly Queue<long> incoming = new();
        private readonly Queue<long> outgoing = new();
        private readonly Queue<double> baselineGaps = new();
        private readonly SortedDictionary<long, GapPoint> points = new();
        private long lastIncoming;
        private long lastOutgoing;
        private long lastBaselineUpdate;
        private double? baseline;
        private double threshold;
        private GapPoint? lastSpike;
        private long traceDelayMs;

        public FlowKey Key => key;
        public long LastSeen => Math.Max(lastIncoming, lastOutgoing);
        public long LastIncoming => lastIncoming;
        public long LastOutgoing => lastOutgoing;

        public void Observe(long qpc, bool isIncoming, long receivedAt)
        {
            if (!isIncoming)
            {
                if (qpc < lastOutgoing)
                    return;
                lastOutgoing = qpc;
                AddPacket(outgoing, qpc);
                return;
            }
            if (qpc <= lastIncoming)
                return;
            AddPacket(incoming, qpc);
            traceDelayMs = (long)Math.Clamp((receivedAt - qpc) * 1000.0 / Second, 0, 60_000);

            if (lastIncoming != 0)
            {
                var gap = new GapPoint(qpc, (qpc - lastIncoming) * 1000.0 / Second);
                var bucket = qpc / Bucket;
                if (!points.TryGetValue(bucket, out var peak) || gap.Ms > peak.Ms)
                    points[bucket] = gap;
                while (points.Count > 64)
                    points.Remove(points.First().Key);

                if (baseline is not null && gap.Ms >= threshold)
                {
                    if (lastSpike is null || qpc - lastSpike.Qpc > 3 * Second || gap.Ms >= lastSpike.Ms)
                        lastSpike = gap;
                }

                // A bounded robust history can adapt to cadence shifts without
                // letting a single spike define the baseline.
                if (gap.Ms <= 1000)
                {
                    baselineGaps.Enqueue(gap.Ms);
                    if (baselineGaps.Count > 128)
                        baselineGaps.Dequeue();
                }
                if (baselineGaps.Count >= 24 && qpc - lastBaselineUpdate >= Second)
                {
                    var sorted = baselineGaps.ToArray();
                    Array.Sort(sorted);
                    baseline = sorted[sorted.Length / 2];
                    double p90 = sorted[(sorted.Length - 1) * 9 / 10];
                    threshold = Math.Max(Math.Max(3 * baseline.Value, 2 * p90), baseline.Value + 35);
                    lastBaselineUpdate = qpc;
                }
            }
            lastIncoming = qpc;
        }

        private static void AddPacket(Queue<long> queue, long qpc)
        {
            queue.Enqueue(qpc);
            while (queue.Count > 2048 || queue.Peek() < qpc - 2 * Second)
                queue.Dequeue();
        }

        public int BidirectionalRate(long now)
        {
            long since = now - 2 * Second;
            while (incoming.TryPeek(out long received) && received < since)
                incoming.Dequeue();
            while (outgoing.TryPeek(out long sent) && sent < since)
                outgoing.Dequeue();
            return Math.Min(incoming.Count, outgoing.Count);
        }

        public bool Eligible(long now) => now - firstSeen >= 5 * Second && BidirectionalRate(now) >= 10;

        public NetworkSample Sample(long now, int eventsLost)
        {
            var recentSpike = lastSpike is { } spike && now - spike.Qpc <= 4 * Second ? spike : null;
            string status = now - lastIncoming > 2 * Second || baseline is null ? "insufficient"
                : recentSpike is not null ? "spike" : "stable";
            return new(now, status, key.Endpoint, key.Local.ToString(), baseline, recentSpike,
                points.Values.Where(point => now - point.Qpc <= 15 * Second).ToArray(), eventsLost, traceDelayMs);
        }
    }
}
