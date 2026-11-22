using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace GameDesk;

internal record PingResult(long Qpc, long? Ms, string Status);
internal record ProbePair(PingResult? Match, PingResult? Gateway);

internal sealed class LatencyProbe
{
    private string? endpoint;
    private string? localAddress;
    private IPAddress? remote;
    private IPAddress? gateway;
    private Task<ProbePair>? pending;
    private ProbePair? last;
    private long nextProbe;

    public ProbePair? Update(NetworkSample sample, CancellationToken cancellation)
    {
        if (sample.Endpoint != endpoint || sample.LocalAddress != localAddress)
        {
            endpoint = sample.Endpoint;
            localAddress = sample.LocalAddress;
            remote = endpoint is not null && IPEndPoint.TryParse(endpoint, out var match) ? match.Address : null;
            gateway = null;
            pending = null;
            last = null;
            nextProbe = 0;
        }

        if (pending is { IsCompletedSuccessfully: true })
        {
            last = pending.Result;
            pending = null;
        }

        if (remote is not null && pending is null && sample.Qpc >= nextProbe)
        {
            if (gateway is null && IPAddress.TryParse(localAddress, out var source))
                gateway = FindGateway(source);
            pending = ProbeBoth(remote, gateway, cancellation);
            nextProbe = sample.Qpc + 2 * Stopwatch.Frequency;
        }
        return last;
    }

    private static IPAddress? FindGateway(IPAddress source)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                var properties = nic.GetIPProperties();
                if (!properties.UnicastAddresses.Any(ip => ip.Address.Equals(source)))
                    continue;
                return properties.GatewayAddresses.Select(ip => ip.Address).FirstOrDefault(ip =>
                    ip.AddressFamily == source.AddressFamily &&
                    !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any));
            }
        }
        catch (NetworkInformationException)
        {
            // Network changes can invalidate an interface while it is being read.
        }
        return null;
    }

    private static async Task<ProbePair> ProbeBoth(IPAddress remote, IPAddress? gateway, CancellationToken cancellation)
    {
        var match = Probe(remote, cancellation);
        var local = Probe(gateway, cancellation);
        return new(await match.ConfigureAwait(false), await local.ConfigureAwait(false));
    }

    private static async Task<PingResult?> Probe(IPAddress? address, CancellationToken cancellation)
    {
        if (address is null)
            return null;
        long qpc = Stopwatch.GetTimestamp();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(750), cancellationToken: cancellation)
                .ConfigureAwait(false);
            return new(qpc, reply.Status == IPStatus.Success ? reply.RoundtripTime : null, reply.Status.ToString());
        }
        catch (PingException)
        {
            return new(qpc, null, "Unavailable");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
