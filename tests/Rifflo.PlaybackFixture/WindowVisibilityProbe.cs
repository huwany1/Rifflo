using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

internal static class WindowVisibilityProbe
{
    private const uint ObjectShow = 0x8002;
    private const uint ObjectHide = 0x8003;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int Watch(int processId, string path)
    {
        using var target = Process.GetProcessById(processId);
        using var writer = new StreamWriter(path) { AutoFlush = true };
        var dispatcher = Dispatcher.CurrentDispatcher;
        WinEventProc report = (_, eventType, window, objectId, childId, _, eventTime) =>
        {
            if (window == 0 || objectId != 0 || childId != 0)
                return;

            var title = new StringBuilder(240);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().StartsWith("Rifflo", StringComparison.Ordinal))
                return;

            writer.WriteLine(JsonSerializer.Serialize(new
            {
                title = title.ToString(),
                visible = eventType == ObjectShow,
                window = window.ToInt64(),
                eventTime,
                stopwatchTimestamp = Stopwatch.GetTimestamp(),
                stopwatchFrequency = Stopwatch.Frequency,
            }, Json));
        };
        var hook = SetWinEventHook(ObjectShow, ObjectHide, 0, report, (uint)processId, 0, 0);
        if (hook == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            if (target.HasExited)
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }, dispatcher);
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(new { watchingPid = processId }, Json));
            timer.Start();
            Dispatcher.Run();
        }
        finally
        {
            timer.Stop();
            UnhookWinEvent(hook);
            GC.KeepAlive(report);
        }
        return 0;
    }

    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId,
        uint eventThread, uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
}
