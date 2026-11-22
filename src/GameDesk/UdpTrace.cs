using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace GameDesk;

internal sealed class UdpTrace : IDisposable
{
    private readonly TraceEventSession session;
    public Task Completion { get; }
    public int EventsLost => session.EventsLost;

    public UdpTrace(NetworkMonitor monitor)
    {
        session = new TraceEventSession($"GameDesk-UDP-{Environment.ProcessId}-{Guid.NewGuid():N}",
            TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
        {
            BufferSizeMB = 4,
            StopOnDispose = true
        };
        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var source = session.Source;
            source.Kernel.UdpIpRecv += data => Receive(data);
            source.Kernel.UdpIpSend += data => Send(data);
            source.Kernel.UdpIpRecvIPV6 += data => ReceiveV6(data);
            source.Kernel.UdpIpSendIPV6 += data => SendV6(data);
            Completion = Task.Factory.StartNew(() => source.Process(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

            void Receive(UdpIpTraceData data)
            {
                if (data.ProcessID == monitor.Target)
                    monitor.Observe(data.ProcessID, Timestamp(data), true, data.daddr, data.dport, data.saddr, data.sport);
            }
            void Send(UdpIpTraceData data)
            {
                if (data.ProcessID == monitor.Target)
                    monitor.Observe(data.ProcessID, Timestamp(data), false, data.saddr, data.sport, data.daddr, data.dport);
            }
            void ReceiveV6(UpdIpV6TraceData data)
            {
                if (data.ProcessID == monitor.Target)
                    monitor.Observe(data.ProcessID, Timestamp(data), true, data.daddr, data.dport, data.saddr, data.sport);
            }
            void SendV6(UpdIpV6TraceData data)
            {
                if (data.ProcessID == monitor.Target)
                    monitor.Observe(data.ProcessID, Timestamp(data), false, data.saddr, data.sport, data.daddr, data.dport);
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Dispose() => session.Dispose();

    // The shared timeline needs Windows QPC ticks, independent of the ETW session epoch.
#pragma warning disable CS0618
    private static long Timestamp(TraceEvent packet) => packet.TimeStampQPC;
#pragma warning restore CS0618

    public static int FindGame(int desktopSession)
    {
        int target = 0;
        foreach (var game in Process.GetProcessesByName("eFootball"))
        {
            using (game)
            {
                try
                {
                    if (game.SessionId == desktopSession && !game.HasExited)
                        target = game.Id;
                }
                catch (InvalidOperationException)
                {
                    // The game can exit between enumeration and reading its session.
                }
            }
        }
        return target;
    }
}
