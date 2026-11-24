using System.Runtime.InteropServices;
using Rifflo.Overlay;
using Rifflo.Settings;

var primary = new WorkArea(0, 0, 1920, 1040, 96, "primary");
var offsetPrimary = new WorkArea(48, 64, 1920, 1030, 144, "primary");
var second = new WorkArea(-2560, -180, 0, 1220, 192, "secondary");

CheckPlacement("96 DPI with 24 px margin", null, null, primary, new(24, 956), false);
CheckPlacement("144 DPI with 36 px margin and offset work area", null, null, offsetPrimary, new(84, 904), false);
CheckPlacement("192 DPI with 48 px margin and negative origin", null, null, second, new(-2512, 1052), false);
var secondaryPosition = new OverlayPosition(-2520, 96) { MonitorId = "SECONDARY" };
CheckPlacement("Saved point survives on left-of-primary screen", secondaryPosition, second, primary, secondaryPosition, false);
CheckPlacement("Legacy saved point without monitor ID survives", new(-2520, 96), second, primary, new(-2520, 96), false);
CheckPlacement("Removed monitor recovers onto current primary", secondaryPosition, null, offsetPrimary, new(84, 904), true);
CheckPlacement("Removed monitor recovers when another monitor takes its coordinates", secondaryPosition,
    second with { MonitorId = "replacement" }, offsetPrimary, new(84, 904), true);
CheckPlacement("Wider layout beside the edge only moves left as far as needed", new(1730, 80), offsetPrimary, offsetPrimary, new(1545, 80), false);
CheckPlacement("Saved point behind a shifted taskbar moves to the edge of the work area", new(20, 80), offsetPrimary, offsetPrimary, new(48, 80), false);
CheckPlacement("Saved integer coordinate outside all monitors recovers", new(int.MaxValue, 96), null, primary, new(24, 956), true);

var edge = new OverlayPosition(-500, 1100);
CheckPlacement("Window fits exactly on the right and bottom boundaries", edge, second, primary, edge, false);
CheckPlacement("DPI increase shifts a previously fitting position just inside the work area", new(1530, 955), primary with { Dpi = 192 },
    offsetPrimary, new(1420, 920), false);
CheckPlacement("Expansion clamps both edges on a negative-coordinate monitor", new(-145, 1190), second, primary, new(-500, 1100), false);
CheckPlacement("An oversized component starts at the work area's top-left", new(-145, 1190), second, primary,
    new(-2560, -180), false, width: 1500, height: 800);
var narrow = new WorkArea(20, 60, 380, 560, 144);
if (narrow.WidthDip != 240)
    throw new InvalidOperationException("Work-area width did not convert to the target monitor's DIP scale.");
Console.WriteLine("PASS  Physical work-area width converts to target-monitor DIPs");

var temporary = Directory.CreateTempSubdirectory("RiffloPlacement-");
var settingsFile = Path.Combine(temporary.FullName, "settings.json");
try
{
    File.WriteAllText(settingsFile, """{"opacity":0}""");
    var store = new SettingsStore(settingsFile);
    if (store.Load().Opacity != 0.2 || !store.Save(AppSettings.Defaults) || store.Load().Opacity != 0.95)
        throw new InvalidOperationException("Zero opacity recovery or default settings round-trip failed.");
    Console.WriteLine("PASS  Stored opacity 0 recovers to 20%; defaults restore 95%");

    File.WriteAllText(settingsFile, """{"position":{"x":-2520,"y":96},"fontSize":28}""");
    var legacy = store.Load();
    if (legacy.Position != new OverlayPosition(-2520, 96) || legacy.FontSize != 28 || legacy.PositionLocked)
        throw new InvalidOperationException("Loading a legacy position altered the coordinates or the saved appearance.");
    var moved = legacy with { Position = secondaryPosition };
    if (!store.Save(moved) || store.Load() != moved)
        throw new InvalidOperationException("Saving from a drag did not persist the new position and monitor with the existing appearance.");
    Console.WriteLine("PASS  Legacy position loads and a new position round-trips with its monitor ID");
}
finally
{
    File.Delete(settingsFile);
    temporary.Delete();
}

if (args.Contains("--desktop-layout"))
    DesktopProbe.Print();

static void CheckPlacement(string label, OverlayPosition? saved, WorkArea? savedArea, WorkArea primaryArea, OverlayPosition expected, bool recovered,
    double width = 250, double height = 60)
{
    var actual = PlacementMath.Resolve(saved, savedArea, primaryArea, width, height);
    if (actual != (expected, recovered))
        throw new InvalidOperationException($"{label} expected {(expected, recovered)} but observed {actual}");
    Console.WriteLine($"PASS  {label} ({actual.Position.X}, {actual.Position.Y})");
}

internal static class DesktopProbe
{
    public static void Print()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var primary = DesktopWindow.FindPrimaryWorkArea();
            var count = 0;
            if (!EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref DesktopWindow.Rect bounds, nint _) =>
            {
                if (DesktopWindow.GetWorkArea(monitor) is { } work)
                    Console.WriteLine($"DESKTOP  Monitor {++count}, primary={work == primary}, bounds=[{bounds.Left}, {bounds.Top}, {bounds.Right}, {bounds.Bottom}], work=[{work.Left}, {work.Top}, {work.Right}, {work.Bottom}], dpi={work.Dpi}");
                return true;
            }, 0))
                throw new InvalidOperationException("Could not enumerate the current desktop monitor layout.");
        }
        finally
        {
            if (previous != 0)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    private delegate bool MonitorCallback(nint monitor, nint deviceContext, ref DesktopWindow.Rect bounds, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint deviceContext, nint clip, MonitorCallback callback, nint parameter);
}
