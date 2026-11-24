using System.Diagnostics;
using Rifflo.Media;

namespace Rifflo.Overlay;

internal readonly record struct OverlayPlayback(bool ShowReal, bool ShowPreview)
{
    internal static readonly TimeSpan PauseDuration = TimeSpan.FromSeconds(2);

    internal static TimeSpan PauseRemaining(long pausedAt, long now) => PauseDuration - Stopwatch.GetElapsedTime(pausedAt, now);

    internal static OverlayPlayback Resolve(TrackState track, bool pauseTail, bool manuallyHidden, bool settingsVisible)
    {
        bool active = track.IsPlaying || track.IsTransition;
        bool real = !manuallyHidden && track.HasTitle && (active || pauseTail);
        return new(real, !real && !active && settingsVisible);
    }
}
