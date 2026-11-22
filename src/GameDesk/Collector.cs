using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace GameDesk;

internal static class Collector
{
    private const string Prefix = "GameDesk-";

    public static NamedPipeServerStream CreateServer(out string token)
    {
        token = Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
        var acl = new PipeSecurity();
        acl.SetOwner(sid);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Prefix + token, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, acl);
    }

    public static async Task RunAsync(string token, int parentPid)
    {
        if (!Guid.TryParseExact(token, "N", out _))
            return;
        using var parent = Process.GetProcessById(parentPid);
        using var quit = new CancellationTokenSource();
        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => quit.Cancel();
        if (parent.HasExited)
            return;

        await using var pipe = new NamedPipeClientStream(".", Prefix + token, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000, quit.Token).ConfigureAwait(false);
        UdpTrace? trace = null;
        try
        {
            var monitor = new NetworkMonitor();
            var probe = new LatencyProbe();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            int lost = 0;
            long nextRefresh = 0;
            do
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextRefresh)
                {
                    int target = UdpTrace.FindGame(parent.SessionId);
                    if (target != monitor.Target)
                    {
                        trace?.Dispose();
                        trace = null;
                        monitor.SetTarget(target);
                        if (target != 0)
                            trace = new UdpTrace(monitor);
                    }
                    lost = trace?.EventsLost ?? 0;
                    nextRefresh = now + Stopwatch.Frequency;
                }
                if (trace is not null && trace.Completion.IsCompleted)
                {
                    await trace.Completion.ConfigureAwait(false);
                    throw new IOException("Windows stopped the UDP trace.");
                }
                var sample = monitor.Sample(now, lost);
                sample = sample with { Probes = probe.Update(sample, quit.Token) };
                if (!await SendAsync(sample).ConfigureAwait(false))
                    return;
            } while (await timer.WaitForNextTickAsync(quit.Token).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (pipe.IsConnected && !quit.IsCancellationRequested)
            {
                var sample = new NetworkSample(Stopwatch.GetTimestamp(), "error", Error: error.GetBaseException().Message);
                await SendAsync(sample).ConfigureAwait(false);
            }
        }
        finally
        {
            trace?.Dispose();
        }

        async Task<bool> SendAsync(NetworkSample sample)
        {
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sample) + "\n");
            try
            {
                await pipe.WriteAsync(json, quit.Token).ConfigureAwait(false);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
