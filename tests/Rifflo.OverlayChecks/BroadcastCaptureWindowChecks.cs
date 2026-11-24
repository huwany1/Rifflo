using System.Runtime.InteropServices;
using System.Windows.Interop;
using Rifflo.Overlay;
using Rifflo.Settings;

internal static class BroadcastCaptureWindowChecks
{
    private const int ExtendedStyle = -20;
    private const long Transparent = 0x20;
    private const long ToolWindow = 0x80;
    private const long Layered = 0x00080000;
    private const long NoActivate = 0x08000000;

    public static void Run()
    {
        using var frames = new OverlayFrameClock();
        var real = new OverlayWindow(preview: false, frames);
        var preview = new OverlayWindow(preview: true, frames);

        try
        {
            var settings = AppSettings.Defaults with { BroadcastCaptureEnabled = true, PositionLocked = true };
            ApplySettings(settings);

            // Force WPF to create real HWNDs without showing either window on the desktop.
            var realInterop = new WindowInteropHelper(real);
            var previewInterop = new WindowInteropHelper(preview);
            var realHandle = realInterop.EnsureHandle();
            var previewHandle = previewInterop.EnsureHandle();
            Require(realHandle != previewHandle && realHandle != 0 && previewHandle != 0,
                "Real and preview overlays must have separate valid HWNDs.");

            CheckWindows(settings);
            foreach (var (broadcast, locked, live) in new[]
            {
                (true, false, true),
                (true, true, true),
                (false, true, true),
                (false, false, true),
                (true, false, true),
                (true, false, false),
                (false, false, false)
            })
            {
                settings = settings with { BroadcastCaptureEnabled = broadcast, PositionLocked = locked, LiveCaptureEnabled = live };
                ApplySettings(settings);
                CheckWindows(settings);
            }

            void CheckWindows(AppSettings state)
            {
                Require(realInterop.Handle == realHandle && previewInterop.Handle == previewHandle,
                    "Hot changes must preserve both original HWNDs.");
                CheckStyle(realHandle, expectedToolWindow: !state.BroadcastCaptureEnabled, state.PositionLocked, "real");
                CheckStyle(previewHandle, expectedToolWindow: true, state.PositionLocked, "preview");
            }
        }
        finally
        {
            preview.Close();
            real.Close();
        }

        Console.WriteLine("Broadcast capture HWND checks passed: initial settings, hot toggles, locking and preview isolation.");

        void ApplySettings(AppSettings settings)
        {
            real.ApplySettings(settings);
            preview.ApplySettings(settings);
        }
    }

    private static void CheckStyle(nint hwnd, bool expectedToolWindow, bool expectedTransparent, string window)
    {
        var style = GetWindowLongPtr(hwnd, ExtendedStyle).ToInt64();
        Require((style & (Layered | NoActivate)) == (Layered | NoActivate),
            $"The {window} window must keep layered and no-activate styles. Actual style {style:X8}.");
        Require(((style & ToolWindow) != 0) == expectedToolWindow,
            $"The {window} window's tool style should be {expectedToolWindow}. Actual style {style:X8}.");
        Require(((style & Transparent) != 0) == expectedTransparent,
            $"The {window} window's transparent hit testing should be {expectedTransparent}. Actual style {style:X8}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
