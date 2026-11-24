using Rifflo.Settings;

namespace Rifflo.Overlay;

internal static class CaptionMarquee
{
    internal const double HoldSeconds = 2;
    internal const double Speed = 30;

    public static double GetOffset(double distance, CaptionScrollMode mode, double elapsedSeconds) =>
        GetOffset(distance, mode, elapsedSeconds, out _);

    public static double GetOffset(double distance, CaptionScrollMode mode, double elapsedSeconds, out double holdSeconds)
    {
        holdSeconds = 0;
        if (distance <= 0)
            return 0;

        var travel = distance / Speed;
        var turnaround = 2 * HoldSeconds + travel;
        var bounce = mode == CaptionScrollMode.Bounce;
        var elapsed = elapsedSeconds % (turnaround + (bounce ? travel : 0));

        if (elapsed < HoldSeconds)
        {
            holdSeconds = HoldSeconds - elapsed;
            return 0;
        }

        if (bounce && elapsed >= turnaround)
            return Math.Clamp(distance - Speed * (elapsed - turnaround), 0, distance);

        if (elapsed >= HoldSeconds + travel)
            holdSeconds = turnaround - elapsed;
        return Math.Clamp(Speed * (elapsed - HoldSeconds), 0, distance);
    }
}
