using System.Diagnostics;

namespace Rifflo.Media;

internal sealed class PlaybackTransition
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(TrackState.TransitionMilliseconds);

    internal TrackState Current { get; private set; } = TrackState.Empty;
    internal long Generation { get; private set; }

    internal bool CanHold(MediaCandidate candidate) => Current.SessionId == candidate.Id && candidate.Id != 0 &&
        (Current.IsPlaying || Current.IsTransition) && IsTemporary(candidate.State, candidate.HasTitle);

    internal TrackState Observe(TrackState next, long observedAt)
    {
        var previous = Current;
        if (next.IsSameSession(previous) && CanHold(new(next.SessionId, next.Source, next.State, next.HasTitle)) &&
            (!previous.IsTransition || Remaining(observedAt) > TimeSpan.Zero))
        {
            if (!previous.IsTransition)
                Generation++;

            next = next with
            {
                IsTransition = true,
                ObservedAt = previous.IsTransition ? previous.ObservedAt : observedAt,
                Title = next.HasTitle ? next.Title : previous.Title,
                Artist = next.HasTitle ? next.Artist : previous.Artist,
                CaptionRevision = next.HasTitle ? next.CaptionRevision : previous.CaptionRevision
            };
        }
        else
        {
            if (next.State is PlayState.None or PlayState.Stopped or PlayState.Changing or PlayState.Closed)
                next = next with { Title = "", Artist = "" };
            else if (next.State == PlayState.Paused && !next.HasTitle && next.IsSameSession(previous))
                next = next with { Title = previous.Title, Artist = previous.Artist, CaptionRevision = previous.CaptionRevision };

            next = next with
            {
                AudioEligible = next.IsPlaying && next.AudioEligible,
                ObservedAt = next.State == PlayState.Paused && previous.State == PlayState.Paused && next.IsSameSession(previous)
                    ? previous.ObservedAt : observedAt
            };
        }

        if ((next with { ObservedAt = previous.ObservedAt }) != previous)
            Current = next;
        return Current;
    }

    internal TrackState? Expire(long now, long? expectedGeneration = null)
    {
        if (!Current.IsTransition || (expectedGeneration.HasValue && expectedGeneration != Generation) || Remaining(now) > TimeSpan.Zero)
            return null;

        Current = Current with { IsTransition = false, AudioEligible = false, Title = "", Artist = "", ObservedAt = now };
        return Current;
    }

    internal TimeSpan Remaining(long now) => Duration - Stopwatch.GetElapsedTime(Current.ObservedAt, now);

    private static bool IsTemporary(PlayState state, bool hasTitle) =>
        state is PlayState.Stopped or PlayState.Changing || (state == PlayState.Playing && !hasTitle);
}
