using Rifflo.Audio;
using Rifflo.Media;
using Rifflo.Settings;

namespace Rifflo.Overlay;

internal enum SpectrumMode { None, Hidden, Disabled, Connecting, Live, Muted, ManualPreset, AutomaticPreset, Pausing, Preview, Transitioning }

internal readonly record struct SpectrumPresentation(SpectrumMode Mode, AudioCaptureReason Reason = AudioCaptureReason.None)
{
    public bool UsesPreset => Mode is SpectrumMode.ManualPreset or SpectrumMode.AutomaticPreset or SpectrumMode.Preview;

    public bool DrawsFrames(bool displayEnabled, bool hasLevels) => Mode switch
    {
        SpectrumMode.Live or SpectrumMode.ManualPreset or SpectrumMode.AutomaticPreset => true,
        SpectrumMode.Preview => displayEnabled,
        SpectrumMode.Pausing or SpectrumMode.Muted or SpectrumMode.Transitioning => hasLevels,
        _ => false
    };

    public static bool WantsCapture(AppSettings settings, TrackState track, bool playing, bool manuallyHidden) =>
        playing && !track.IsTransition && CaptureEnabled(settings, track, manuallyHidden);

    internal static bool CaptureEnabled(AppSettings settings, TrackState track, bool manuallyHidden) =>
        !manuallyHidden && settings.SpectrumEnabled && settings.LiveCaptureEnabled &&
        track.AudioEligible && !string.IsNullOrWhiteSpace(track.Source);

    public static SpectrumPresentation Resolve(AppSettings settings, TrackState track, bool playing, bool manuallyHidden,
        bool showPauseSpectrum, bool showPreview, AudioCaptureStatus capture, long requestedGeneration, bool audioFailed = false)
    {
        if (track.IsTransition)
        {
            if (manuallyHidden)
                return new(SpectrumMode.Hidden);
            if (!settings.SpectrumEnabled)
                return new(SpectrumMode.Disabled);

            return CaptureEnabled(settings, track, manuallyHidden) && capture.State == AudioCaptureState.Live &&
                capture.Generation == requestedGeneration && MediaSelection.SourceEquals(capture.Source, track.Source)
                ? new(SpectrumMode.Live) : new(SpectrumMode.Transitioning);
        }
        if (!playing)
        {
            if (showPauseSpectrum)
                return new(SpectrumMode.Pausing);
            return new(showPreview ? SpectrumMode.Preview : SpectrumMode.None);
        }
        if (manuallyHidden)
            return new(SpectrumMode.Hidden);
        if (!settings.SpectrumEnabled)
            return new(SpectrumMode.Disabled);
        if (!settings.LiveCaptureEnabled)
            return new(SpectrumMode.ManualPreset);
        if (string.IsNullOrWhiteSpace(track.Source))
            return new(SpectrumMode.AutomaticPreset, AudioCaptureReason.SourceUnavailable);
        if (!track.AudioEligible)
            return new(SpectrumMode.AutomaticPreset, AudioCaptureReason.AmbiguousSource);
        if (audioFailed)
            return new(SpectrumMode.AutomaticPreset, AudioCaptureReason.CaptureFailed);
        if (capture.Generation != requestedGeneration || !MediaSelection.SourceEquals(capture.Source, track.Source))
            return new(SpectrumMode.Connecting);

        return capture.State switch
        {
            AudioCaptureState.Live => new(SpectrumMode.Live),
            AudioCaptureState.Muted => new(SpectrumMode.Muted),
            AudioCaptureState.Fallback => new(SpectrumMode.AutomaticPreset, capture.Reason),
            _ => new(SpectrumMode.Connecting)
        };
    }
}
