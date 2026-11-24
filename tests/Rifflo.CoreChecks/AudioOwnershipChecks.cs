using Rifflo.Audio;
using Rifflo.Media;

internal static class AudioOwnershipChecks
{
    private const string AppWindow = "MSEdge._/player.html.edgeprofile.Default";
    private const string OrdinaryTab = "MSEdge";
    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    public static void CompetitionFromMedia()
    {
        var selected = new MediaCandidate(1, AppWindow, PlayState.Playing);
        MediaCandidate[] candidates =
        [
            selected, new(2, OrdinaryTab, PlayState.Playing), new(3, "UnknownSource", PlayState.None),
            new(4, "PausedSource", PlayState.Paused), new(5, "StoppedSource", PlayState.Stopped), new(6, "", PlayState.Playing)
        ];
        var competition = MediaSelection.FindAudioCompetition(selected, candidates);
        Require(competition.Equals(AudioCompetition.FromSources(["unknownsource", "msedge", "MSEDGE"])),
            "Playing and unknown identities participate, independently of enumeration order and case.");
        Require(MediaSelection.CanIdentifyAudio(selected, candidates), "Distinct media identities leave ownership to process matching.");
        Require(!MediaSelection.CanIdentifyAudio(selected, [.. candidates, new(7, AppWindow, PlayState.None)]),
            "Two plausible same-AUMID sessions still fail before process matching.");
        var one = new TrackState(AppWindow, "Title", "", PlayState.Playing) { AudioCompetition = competition };
        var two = one with { AudioCompetition = MediaSelection.FindAudioCompetition(selected, candidates.Reverse()) };
        Require(one == two, "Repeating the same competitor set does not look like a new media selection.");
    }

    public static void ProcessOwnership()
    {
        var window = Process(10, 0, 100, windowIds: [AppWindow]);
        var renderer = Process(11, 10, 110);
        var otherWindowInTree = Process(12, 10, 120, windowIds: [OrdinaryTab]);
        var processes = new Dictionary<uint, ProcessIdentity> { [10] = window, [11] = renderer, [12] = otherWindowInTree };
        ActiveAudioSession[] sessions = [new(11, false)];

        int lookups = 0;
        IReadOnlyList<string> Resolve(string source)
        {
            lookups++;
            return source == OrdinaryTab ? [EdgePath] : [];
        }

        Require(AudioSourceMatcher.Match(AppWindow, processes, sessions, Resolve).State == MatchState.Matched && lookups == 0,
            "A single registered media session still captures its confirmed Edge process tree.");
        Require(AudioSourceMatcher.Match(AppWindow, processes, sessions, Resolve, competitors: [OrdinaryTab]) is
            { State: MatchState.Ambiguous, Reason: MatchReason.ConflictingIdentity } && lookups == 0,
            "A competing app identity on a sibling of the shared renderer disqualifies capture.");
        processes[11] = renderer with { WindowAppIds = [OrdinaryTab] };
        Require(AudioSourceMatcher.Match(AppWindow, processes, sessions, Resolve, competitors: [OrdinaryTab]).State == MatchState.Ambiguous,
            "A competing identity attached to the audio child disqualifies capture.");
        processes[11] = renderer;

        processes.Remove(12);
        Require(AudioSourceMatcher.Match(AppWindow, processes, sessions, Resolve, competitors: [OrdinaryTab]) is
            { State: MatchState.Ambiguous, Reason: MatchReason.ConflictingIdentity } && lookups == 1,
            "The same registered executable leaves ownership ambiguous when the competitor has no disjoint verified owner.");
        Require(AudioSourceMatcher.Match(AppWindow, processes, [new(11, true)], Resolve, competitors: [OrdinaryTab]).State == MatchState.Ambiguous,
            "An unresolved competitor makes the shared tree ambiguous even if its audio session is muted.");

        processes[20] = Process(20, 0, 200, windowIds: [OrdinaryTab]);
        processes[21] = Process(21, 20, 210);
        ActiveAudioSession[] disjointSessions = [.. sessions, new(21, false)];
        var beforeDisjoint = lookups;
        var selected = AudioSourceMatcher.Match(AppWindow, processes, disjointSessions, Resolve, competitors: [OrdinaryTab]);
        var competing = AudioSourceMatcher.Match(OrdinaryTab, processes, disjointSessions, Resolve, competitors: [AppWindow]);
        Require(selected is { State: MatchState.Matched, Process.Id: 10 } &&
            competing is { State: MatchState.Matched, Process.Id: 20 } && lookups == beforeDisjoint,
            "Verified disjoint audio ownership permits both identities even when they run the same installed executable.");

        processes[12] = otherWindowInTree with { CreatedAt = 0 };
        Require(AudioSourceMatcher.Match(AppWindow, processes, disjointSessions, Resolve, competitors: [OrdinaryTab]).State == MatchState.Ambiguous,
            "A potentially overlapping, unverified process cannot authorize a competing stream in the selected tree.");
    }

    internal static AudioMatch BrowserMatch(AudioCompetition competition)
    {
        var processes = new Dictionary<uint, ProcessIdentity>
        {
            [10] = Process(10, 0, 100, windowIds: [AppWindow]),
            [11] = Process(11, 10, 110)
        };
        return AudioSourceMatcher.Match(AppWindow, processes, [new(11, false)],
            _ => [EdgePath], competitors: competition.Sources);
    }

    private static ProcessIdentity Process(uint id, uint parent, long started, string[]? windowIds = null) =>
        new(id, parent, started, EdgePath, "", "", windowIds ?? []);

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}
