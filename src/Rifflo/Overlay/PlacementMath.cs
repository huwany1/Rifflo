using Rifflo.Settings;

namespace Rifflo.Overlay;

internal readonly record struct WorkArea(int Left, int Top, int Right, int Bottom, uint Dpi, string? MonitorId = null)
{
    internal double WidthDip => (Right - Left) * 96.0 / Dpi;

    internal bool MatchesMonitor(OverlayPosition position) => position.MonitorId is null ||
        string.Equals(position.MonitorId, MonitorId, StringComparison.OrdinalIgnoreCase);
}

internal static class PlacementMath
{
    internal const int MarginDip = 24;

    internal static (OverlayPosition Position, bool Recovered) Resolve(
        OverlayPosition? savedPosition, WorkArea? savedWorkArea, WorkArea primary, double width, double height)
    {
        if (savedPosition is not null && savedWorkArea is { } area && area.MatchesMonitor(savedPosition))
            return (ClampToWorkArea(savedPosition, area, width, height), false);

        var margin = ToPixels(MarginDip, primary.Dpi);
        var x = primary.Left + margin;
        var y = primary.Bottom - margin - ToPixels(height, primary.Dpi);
        return (ClampToWorkArea(new OverlayPosition(x, y), primary, width, height), savedPosition is not null);
    }

    private static OverlayPosition ClampToWorkArea(OverlayPosition position, WorkArea area, double width, double height)
    {
        var rightmost = Math.Max(area.Left, area.Right - ToPixels(width, area.Dpi));
        var bottommost = Math.Max(area.Top, area.Bottom - ToPixels(height, area.Dpi));
        return position with
        {
            X = Math.Clamp(position.X, area.Left, rightmost),
            Y = Math.Clamp(position.Y, area.Top, bottommost)
        };
    }

    internal static int ToPixels(double dip, uint dpi) => (int)Math.Round(dip * dpi / 96.0);
}
