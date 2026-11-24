using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class RiffloVerificationNative
{
    public sealed class WindowInfo
    {
        public long Hwnd { get; set; }
        public uint ProcessId { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
        public bool Visible { get; set; }
        public long ExtendedStyle { get; set; }
        public Rect Bounds { get; set; }
        public uint Dpi { get; set; }
    }

    public sealed class MonitorInfo
    {
        public Rect Bounds { get; set; }
        public Rect Work { get; set; }
        public bool Primary { get; set; }
        public string Device { get; set; }
        public uint RefreshHz { get; set; }
    }

    public sealed class ScreenSample
    {
        public long Timestamp { get; set; }
        public uint RgbHash { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public Rect Bounds;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    // DEVMODEW offsets for the only fields needed by EnumDisplaySettings.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public uint Frequency;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression, ImageSize, XPels, YPels, ColorsUsed, ImportantColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public Rect CaretBounds;
    }

    public static WindowInfo[] Windows(uint processId)
    {
        var windows = new List<WindowInfo>();
        WithPhysicalCoordinates(() => EnumWindows((hwnd, ignored) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (processId == 0 || pid == processId)
            {
                windows.Add(Inspect(hwnd, pid));
            }

            return true;
        }, IntPtr.Zero));
        return windows.ToArray();
    }

    public static WindowInfo Foreground()
    {
        return WithPhysicalCoordinates(() =>
        {
            var hwnd = GetForegroundWindow();
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return Inspect(hwnd, pid);
        });
    }

    public static long KeyboardFocus()
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(0, ref info) ? info.Focus.ToInt64() : 0;
    }

    public static MonitorInfo[] Monitors()
    {
        var monitors = new List<MonitorInfo>();
        WithPhysicalCoordinates(() => EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr parameter) =>
            {
                var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
                    var refresh = EnumDisplaySettings(info.Device, -1, ref mode) ? mode.Frequency : 0;
                    monitors.Add(new MonitorInfo
                    {
                        Bounds = info.Bounds, Work = info.Work, Primary = (info.Flags & 1) != 0,
                        Device = info.Device, RefreshHz = refresh
                    });
                }
                return true;
            }, IntPtr.Zero));
        return monitors.ToArray();
    }

    public static WindowInfo WindowAt(int x, int y)
    {
        return WithPhysicalCoordinates(() =>
        {
            var hwnd = GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2);
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return Inspect(hwnd, pid);
        });
    }

    public static bool? MenuItemChecked(int x, int y)
    {
        return WithPhysicalCoordinates<bool?>(() =>
        {
            IAccessibleMenuItem item;
            object child;
            if (AccessibleObjectFromPoint(new Point { X = x, Y = y }, out item, out child) < 0 || item == null)
                return null;
            try
            {
                var role = item.GetAccRole(child);
                if (!(role is int) || (int)role != 0x0C) // ROLE_SYSTEM_MENUITEM
                    return null;
                var state = item.GetAccState(child);
                return state is int ? ((int)state & 0x10) != 0 : (bool?)null; // STATE_SYSTEM_CHECKED
            }
            finally { Marshal.ReleaseComObject(item); }
        });
    }

    public static ScreenSample[] SampleDesktopPixels(int x, int y, int width, int height, double seconds)
    {
        return WithPhysicalCoordinates(() =>
        {
            var screen = GetDC(IntPtr.Zero);
            var memory = CreateCompatibleDC(screen);
            var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            IntPtr image;
            var bitmap = CreateDIBSection(memory, ref header, 0, out image, IntPtr.Zero, 0);
            var old = SelectObject(memory, bitmap);
            try
            {
                if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero || image == IntPtr.Zero)
                    throw new InvalidOperationException("Could not allocate desktop sample buffer.");
                var pixels = new int[width * height];
                var samples = new List<ScreenSample>();
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < seconds)
                {
                    if (DwmFlush() != 0 || !BitBlt(memory, 0, 0, width, height, screen, x, y, 0x40CC0020u))
                        throw new InvalidOperationException("Could not sample the composed screen region.");
                    var timestamp = Stopwatch.GetTimestamp();
                    GdiFlush();
                    Marshal.Copy(image, pixels, 0, pixels.Length);
                    var hash = 2166136261u;
                    foreach (var pixel in pixels)
                        hash = unchecked((hash ^ ((uint)pixel & 0x00FFFFFFu)) * 16777619u);
                    samples.Add(new ScreenSample { Timestamp = timestamp, RgbHash = hash });
                }
                return samples.ToArray();
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memory, old);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        });
    }

    public static bool Activate(long hwnd)
    {
        return SetForegroundWindow(new IntPtr(hwnd));
    }

    public static void Click(int x, int y, bool right)
    {
        WithPhysicalCoordinates(() =>
        {
            Point old;
            GetCursorPos(out old);
            SetCursorPos(x, y);
            Thread.Sleep(65);
            mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(35);
            mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(75);
            SetCursorPos(old.X, old.Y);
            return true;
        });
    }

    public static void Drag(int x, int y, int dx, int dy)
    {
        var old = Cursor();
        BeginDrag(x, y);
        try
        {
            for (var step = 1; step <= 8; step++)
            {
                Thread.Sleep(30);
                MovePointer(x + dx * step / 8, y + dy * step / 8);
            }
        }
        finally
        {
            ReleaseDrag();
            Thread.Sleep(75);
            MovePointer(old.X, old.Y);
        }
    }

    public static Point Cursor()
    {
        return WithPhysicalCoordinates(() =>
        {
            Point point;
            GetCursorPos(out point);
            return point;
        });
    }

    public static void MovePointer(int x, int y)
    {
        if (!WithPhysicalCoordinates(() => SetCursorPos(x, y)))
            throw new InvalidOperationException("Could not move the cursor to the requested screen point.");
    }

    public static void BeginDrag(int x, int y)
    {
        MovePointer(x, y);
        Thread.Sleep(65);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    }

    public static void ReleaseDrag() => mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);

    public static void SetKey(byte key, bool pressed) =>
        keybd_event(key, 0, pressed ? 0u : 0x0002u, IntPtr.Zero);

    private static WindowInfo Inspect(IntPtr hwnd, uint pid)
    {
        var title = new StringBuilder(512);
        var className = new StringBuilder(128);
        GetWindowText(hwnd, title, title.Capacity);
        GetClassName(hwnd, className, className.Capacity);
        Rect bounds;
        GetWindowRect(hwnd, out bounds);
        return new WindowInfo
        {
            Hwnd = hwnd.ToInt64(), ProcessId = pid, Title = title.ToString(), ClassName = className.ToString(),
            Visible = IsWindowVisible(hwnd), ExtendedStyle = GetWindowLongPtr(hwnd, -20).ToInt64(), Bounds = bounds,
            Dpi = GetDpiForWindow(hwnd),
        };
    }

    private static T WithPhysicalCoordinates<T>(Func<T> action)
    {
        var oldContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return action(); }
        finally { if (oldContext != IntPtr.Zero) SetThreadDpiAwarenessContext(oldContext); }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    private delegate bool EnumMonitorsProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr parameter);

    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IAccessibleMenuItem
    {
        [return: MarshalAs(UnmanagedType.IDispatch)] object GetAccParent();
        int GetAccChildCount();
        [return: MarshalAs(UnmanagedType.IDispatch)] object GetAccChild([MarshalAs(UnmanagedType.Struct)] object child);
        string GetAccName([MarshalAs(UnmanagedType.Struct)] object child);
        string GetAccValue([MarshalAs(UnmanagedType.Struct)] object child);
        string GetAccDescription([MarshalAs(UnmanagedType.Struct)] object child);
        [return: MarshalAs(UnmanagedType.Struct)] object GetAccRole([MarshalAs(UnmanagedType.Struct)] object child);
        [return: MarshalAs(UnmanagedType.Struct)] object GetAccState([MarshalAs(UnmanagedType.Struct)] object child);
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMonitorsProc callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, IntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scanCode, uint flags, IntPtr extraInfo);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(
        Point point, [MarshalAs(UnmanagedType.Interface)] out IAccessibleMenuItem item, [MarshalAs(UnmanagedType.Struct)] out object child);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapHeader header, uint colorUse, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        IntPtr output, int x, int y, int width, int height, IntPtr input, int sourceX, int sourceY, uint flags);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
