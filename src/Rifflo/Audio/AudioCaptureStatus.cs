namespace Rifflo.Audio;

public enum AudioCaptureState { Idle, Connecting, Live, Muted, Fallback }

public enum AudioCaptureReason { None, SourceUnavailable, AmbiguousSource, UntrustedIdentity, MatchTimeout, CaptureFailed, NoPackets }

public sealed record AudioCaptureStatus(
    AudioCaptureState State, AudioCaptureReason Reason, bool Recording, string Source, long Generation);
