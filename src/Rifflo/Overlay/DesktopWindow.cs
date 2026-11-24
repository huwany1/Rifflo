using System.Runtime.InteropServices;
using Rifflo.Settings;

namespace Rifflo.Overlay;

internal static class DesktopWindow
{
    internal const int MouseActivate = 0x0021;
    internal const int HitTest = 0x0084;
    internal const int SettingChange = 0x001A;
    internal const int DisplayChange = 0x007E;
    internal const int NoActivate = 3;
    internal const int HitTransparent = -1;

    private const int ExtendedStyle = -20;
    private const int Transparent = 0x20;
    private const int ToolWindow = 0x80;
    private const int NoActivateStyle = 0x08000000;
    private const uint KeepSize = 0x0001;
    private const uint KeepPosition = 0x0002;
    private const uint KeepOrder = 0x0004;
    private const uint MoveWithoutActivation = 0x0010;
    private const uint FrameChanged = 0x0020;
    private const uint DefaultToPrimary = 1;
    private const uint GetDeviceInterfaceName = 1;
    private const int ActiveMonitor = 1;
    private const int SwapButton = 23;
    private const int LeftButton = 1;
    private const int RightButton = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public int State;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    internal static void Configure(nint window, bool clickThrough, bool toolWindow)
    {
        var previousStyle = GetWindowLongPtr(window, ExtendedStyle).ToInt64();
        var style = previousStyle | NoActivateStyle;
        style = clickThrough ? style | Transparent : style & ~Transparent;
        style = toolWindow ? style | ToolWindow : style & ~ToolWindow;
        if (style == previousStyle)
            return;

        SetWindowLongPtr(window, ExtendedStyle, (nint)style);
        SetWindowPos(window, 0, 0, 0, 0, 0, KeepSize | KeepPosition | KeepOrder | MoveWithoutActivation | FrameChanged);
    }

    internal static void Move(nint window, int x, int y) =>
        SetWindowPos(window, 0, x, y, 0, 0, KeepSize | KeepOrder | MoveWithoutActivation);

    internal static bool IsPrimaryButtonDown() => GetAsyncKeyState(GetSystemMetrics(SwapButton) == 0 ? LeftButton : RightButton) < 0;

    internal static nint MonitorAt(Point position) => MonitorFromPoint(position, 0);

    internal static WorkArea? FindPrimaryWorkArea() => GetWorkArea(MonitorFromPoint(new Point(0, 0), DefaultToPrimary));

    internal static WorkArea? FindWorkArea(Point position) => GetWorkArea(MonitorAt(position));

    internal static WorkArea? FindWorkArea(OverlayPosition position)
    {
        var area = FindWorkArea(new Point(position.X, position.Y));
        return area is { } found && found.MatchesMonitor(position) ? found : null;
    }

    internal static WorkArea? GetWorkArea(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return null;

        var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX : 96;
        return new WorkArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, dpi, MonitorId(info.Device));
    }

    private static string MonitorId(string deviceName)
    {
        // The monitor interface name survives changes to the GDI \\.\DISPLAY number.
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; EnumDisplayDevices(deviceName, index, ref device, GetDeviceInterfaceName); index++)
        {
            if ((device.State & ActiveMonitor) != 0 && !string.IsNullOrEmpty(device.Id))
                return device.Id;
        }
        return deviceName;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool EnumDisplayDevices(string name, uint index, ref DisplayDevice device, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
