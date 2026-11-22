using System.Runtime.InteropServices;

namespace GameDesk;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--collect", var token, var parent] && int.TryParse(parent, out int parentPid))
        {
            try
            {
                Collector.RunAsync(token, parentPid).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or ArgumentException)
            {
                return 1;
            }
        }
        if (args is ["--self-test", var report, var addressFamily])
            return SelfTest.RunAsync(report, addressFamily == "ipv6").GetAwaiter().GetResult();
        if (args is ["--self-test-collector", var collectorReport])
            return SelfTest.CheckCollectorAsync(collectorReport).GetAwaiter().GetResult();
        if (args is ["--self-test-data", var dataReport])
            return SelfTest.CheckData(dataReport);
        if (args is ["--udp-echo", var echoPipe, var family, var control])
            return SelfTest.EchoAsync(echoPipe, family == "ipv6", control).GetAwaiter().GetResult();
        if (args is ["--self-test-overlay", var uiReport, var image])
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.CheckOverlay(uiReport, image);
        }
        if (args is ["--self-test-panel", var panelReport, var panelImage])
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.CheckPanel(panelReport, panelImage);
        }
        if (args is ["--self-test-review", var sessions, var reviewReport, var reviewImage])
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.CheckReview(sessions, reviewReport, reviewImage);
        }

        using var openRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\GameDesk.Windows.Open");
        using var instance = new Mutex(true, "Local\\GameDesk.Windows", out bool acquired);
        if (!acquired)
        {
            nint window = FindWindow(null, ControlWindow.AppName);
            if (window != 0)
            {
                _ = GetWindowThreadProcessId(window, out uint pid);
                if (pid != 0)
                    _ = AllowSetForegroundWindow(pid);
            }
            openRequest.Set();
            return 0;
        }
        ApplicationConfiguration.Initialize();
        bool demo = args is ["--demo"];
        using var app = new TrayApp(demo);
        var reopen = ThreadPool.RegisterWaitForSingleObject(openRequest,
            (_, _) => app.RequestOpenControls(), null, Timeout.Infinite, false);
        try
        {
            if (!demo)
                app.OpenControls();
            Application.Run(app);
        }
        finally
        {
            reopen.Unregister(null);
        }
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
