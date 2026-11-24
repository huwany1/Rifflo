namespace Rifflo.Media;

public enum PlayState { None, Playing, Paused, Stopped, Changing, Closed }

public sealed record TrackState(string Source, string Title, string Artist, PlayState State)
{
    internal const int TransitionMilliseconds = 300;

    public bool AudioEligible { get; init; } = true;
    internal AudioCompetition AudioCompetition { get; init; } = AudioCompetition.Empty;
    internal long CaptionRevision { get; init; }
    internal long SessionId { get; init; }
    internal bool IsTransition { get; init; }
    internal long ObservedAt { get; init; }

    internal bool HasTitle => !string.IsNullOrWhiteSpace(Title);
    internal bool IsPlaying => State == PlayState.Playing && HasTitle && !IsTransition;
    internal bool IsSameSession(TrackState other) => SessionId == other.SessionId && MediaSelection.SourceEquals(Source, other.Source);

    public static TrackState Empty { get; } = new("", "", "", PlayState.None) { AudioEligible = false };
}

internal struct CaptionTrackIdentity
{
    private string? _album;
    private int _number;

    internal long Revision { get; private set; }

    internal void Observe(bool sameCaption, string album, int number)
    {
        if (!sameCaption)
        {
            _album = null;
            _number = 0;
        }

        var albumChanged = _album is not null && album.Length > 0 && !string.Equals(_album, album, StringComparison.OrdinalIgnoreCase);
        var numberChanged = _number > 0 && number > 0 && _number != number;
        if (albumChanged || numberChanged)
        {
            Revision++;
            // The unchanged field can arrive later for the newly identified track.
            _album = albumChanged ? album : null;
            _number = numberChanged ? number : 0;
            return;
        }

        if (album.Length > 0)
            _album = album;
        if (number > 0)
            _number = number;
    }
}

internal readonly record struct MediaCandidate(long Id, string Source, PlayState State, bool HasTitle = true);

internal static class MediaSelection
{
    public static long? FindUniqueSource(IEnumerable<MediaCandidate> candidates, string source)
    {
        if (source.Length == 0)
            return null;

        long? uniqueId = null;
        foreach (var candidate in candidates)
        {
            if (!SourceEquals(candidate.Source, source))
                continue;
            if (uniqueId.HasValue)
                return null;
            uniqueId = candidate.Id;
        }

        return uniqueId;
    }

    public static MediaCandidate? Pick(
        IEnumerable<MediaCandidate> candidates, string preferred, long? current, long? previous, long? transitioning = null) =>
        candidates
            .Where(candidate => candidate.State != PlayState.Closed)
            .OrderByDescending(candidate => candidate.State == PlayState.Playing && candidate.HasTitle)
            .ThenByDescending(candidate => candidate.Id == transitioning)
            .ThenByDescending(candidate => candidate.State == PlayState.Playing)
            .ThenByDescending(candidate => SourceEquals(candidate.Source, preferred) && preferred.Length != 0)
            .ThenByDescending(candidate => candidate.Id == current)
            .ThenByDescending(candidate => candidate.Id == previous)
            .ThenBy(candidate => candidate.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => (MediaCandidate?)candidate)
            .FirstOrDefault();

    public static bool CanIdentifyAudio(MediaCandidate selected, IEnumerable<MediaCandidate> candidates) =>
        selected.Source.Length != 0 && selected.State == PlayState.Playing &&
        !candidates.Any(candidate => MayCompete(candidate, selected) && SourceEquals(candidate.Source, selected.Source));

    public static AudioCompetition FindAudioCompetition(MediaCandidate selected, IEnumerable<MediaCandidate> candidates) =>
        AudioCompetition.FromSources(candidates.Where(candidate => MayCompete(candidate, selected) &&
            !SourceEquals(candidate.Source, selected.Source)).Select(candidate => candidate.Source));

    private static bool MayCompete(MediaCandidate candidate, MediaCandidate selected) =>
        candidate.Id != selected.Id && candidate.State is PlayState.Playing or PlayState.Changing or PlayState.None;

    public static bool SourceEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal struct MetadataGeneration
{
    private long version;

    public long Begin() => ++version;
    public bool IsCurrent(long candidate) => candidate == version;
}
