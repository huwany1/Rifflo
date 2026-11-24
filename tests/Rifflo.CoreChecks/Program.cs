using System.Diagnostics;
using System.Runtime.InteropServices;
using Rifflo.Audio;
using Rifflo.Media;
using Rifflo.Settings;

var tests = new (string Name, Action Check)[]
{
    ("Playback, preferred app, system current, and stable session ordering", CheckSelection),
    ("Older asynchronous metadata discarded after fast track changes", CheckMetadataGeneration),
    ("Same-caption track changes use known album or track number", CheckCaptionTrackIdentity),
    ("Process-tree identity, same executable ambiguity, and PID reuse", CheckIdentity),
    ("Competing AUMIDs preserve media selection and include playing or unknown sessions", AudioOwnershipChecks.CompetitionFromMedia),
    ("AUMIDs sharing a process or executable are gated; disjoint verified trees can capture", AudioOwnershipChecks.ProcessOwnership),
    ("Shell-registered KuGou matches custom paths, upgrades, and restarted processes", CheckRegisteredKuGou),
    ("Signed KuGou fallback authenticates active processes and isolates playback", CheckSignedKuGou),
    ("Unverified process trees, conflicting identities, and multiple active instances fail closed", CheckInferredRejections),
    ("PCM, Hann FFT and low/high logarithmic band separation", CheckBands),
    ("Silence, packet gap decay, and bounded steady capture allocations", CheckSilence),
    ("Timed live spectrum has equal attack and decay at 30/60 FPS and after a UI stall", CheckLiveFrameRate),
    ("Silent and very quiet packets establish live health, with same-source reuse", () => AudioMonitorChecks.PacketHealth().GetAwaiter().GetResult()),
    ("First and sustained packet loss fall back and recover on silent packets", () => AudioMonitorChecks.NoPacketsAndRecovery().GetAwaiter().GetResult()),
    ("Explicit mute and output topology changes revoke and reconnect capture", () => AudioMonitorChecks.MutingAndTopology().GetAwaiter().GetResult()),
    ("Source ambiguity and explicit capture failures fall back and recover", () => AudioMonitorChecks.FailedMatchAndCapture().GetAwaiter().GetResult()),
    ("A blocked matcher times out without blocking a new source or leaking its result", () => AudioMonitorChecks.BlockedMatchAndReplacement().GetAwaiter().GetResult()),
    ("A still-blocked matcher allows bounded recovery of the same source", () => AudioMonitorChecks.BlockedMatchSameSource().GetAwaiter().GetResult()),
    ("A timed-out activation releases its late recorder and retries with backoff", () => AudioMonitorChecks.BlockedActivation().GetAwaiter().GetResult()),
    ("Cancellation suppresses late activation, packets, FFTs, and retries", () => AudioMonitorChecks.CancellationAndLatePackets().GetAwaiter().GetResult()),
    ("A competing AUMID cancels old bands and its departure recovers the same source", () => AudioMonitorChecks.CompetitionChanges().GetAwaiter().GetResult()),
    ("A same-session transition gates samples, clears FFT history and preserves the recorder", () => AudioMonitorChecks.SameSessionTransition().GetAwaiter().GetResult()),
    ("Timeout, pause and changes in ownership or availability revoke a retained recorder", () => AudioMonitorChecks.TransitionRevocation().GetAwaiter().GetResult()),
    ("Missing or invalid settings recover and valid settings round-trip", CheckSettings),
    ("Old settings upgrade and invalid new fields recover independently", CheckSettingsCompatibility),
    ("Broadcast capture defaults off and round-trips independently of live spectrum capture", CheckBroadcastCaptureSettings)
};

int failed = 0;
foreach (var (name, check) in tests)
{
    try
    {
        check();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {name} — {error.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void CheckSelection()
{
    var paused = new MediaCandidate(1, "First", PlayState.Paused);
    var playing = new MediaCandidate(2, "Second", PlayState.Playing);
    var otherPlaying = new MediaCandidate(3, "Third", PlayState.Playing);
    MediaCandidate[] candidates = [paused, playing, otherPlaying];

    Require(MediaSelection.Pick(candidates, "first", 1, 1) == playing, "Playing takes priority over paused preference and system current.");
    Require(MediaSelection.Pick(candidates, "third", 2, 2) == otherPlaying, "Preferred matching playing app takes priority over system current.");
    Require(MediaSelection.Pick(candidates, "", 3, 2) == otherPlaying, "Windows current wins when equally eligible.");
    var projectedCurrent = MediaSelection.FindUniqueSource(candidates, "third");
    Require(MediaSelection.Pick(candidates, "", projectedCurrent, 2) == otherPlaying,
        "A different WinRT current wrapper can be projected by its unique exact app identity.");
    Require(MediaSelection.FindUniqueSource([playing, new(4, "SECOND", PlayState.Playing)], "Second") is null,
        "Repeated app identities cannot determine which session Windows marked current.");
    Require(MediaSelection.FindUniqueSource(candidates, "Sec") is null, "Current identity does not match a prefix.");
    Require(MediaSelection.Pick(candidates, "", null, 3) == otherPlaying, "Retain the previous eligible selection.");
    Require(MediaSelection.Pick(candidates.Reverse(), "", null, null) == playing, "Fallback is stable across enumeration order.");
    Require(MediaSelection.Pick([], "", null, 3) is null, "Removing all sessions clears selection.");
    Require(MediaSelection.CanIdentifyAudio(playing, candidates), "One playing session identifies its application.");
    Require(!MediaSelection.CanIdentifyAudio(playing, [playing, new(4, "SECOND", PlayState.Playing)]),
        "Two playing media sessions in the same app cannot independently identify loopback audio.");
    Require(!MediaSelection.CanIdentifyAudio(paused, candidates), "A paused title does not grant capture.");
}

static void CheckMetadataGeneration()
{
    var generation = new MetadataGeneration();
    var first = generation.Begin();
    var second = generation.Begin();
    var third = generation.Begin();
    string title = "";
    if (generation.IsCurrent(third)) title = "Third track";
    if (generation.IsCurrent(first)) title = "First track";
    if (generation.IsCurrent(second)) title = "Second track";
    Require(title == "Third track", "The latest request remains authoritative when older reads complete afterwards.");
}

static void CheckCaptionTrackIdentity()
{
    var track = new CaptionTrackIdentity();
    track.Observe(sameCaption: false, "", 0);
    track.Observe(sameCaption: true, "Original album", 1);
    track.Observe(sameCaption: true, "", 0);
    track.Observe(sameCaption: true, "original album", 1);
    Require(track.Revision == 0, "Late, omitted and repeated optional fields leave the current caption alone.");

    track.Observe(sameCaption: true, "New album", 0);
    track.Observe(sameCaption: true, "New album", 4);
    track.Observe(sameCaption: true, "New album", 4);
    Require(track.Revision == 1, "A known album change identifies one new track, including when its number arrives later.");

    track.Observe(sameCaption: true, "", 5);
    track.Observe(sameCaption: true, "New album", 5);
    Require(track.Revision == 2, "A known track-number change identifies a new track even when the album is briefly omitted.");

    track.Observe(sameCaption: false, "", 0);
    track.Observe(sameCaption: true, "Another album", 12);
    Require(track.Revision == 2, "A different caption replaces the metadata baseline and avoids an extra restart when its details arrive.");
}

static void CheckIdentity()
{
    var root = new ProcessIdentity(10, 0, 100, @"C:\Player\player.exe", "", "", ["Player.Music"]);
    var renderer = new ProcessIdentity(11, 10, 101, root.ImagePath, "", "", []);
    var other = new ProcessIdentity(20, 0, 102, @"C:\Elsewhere\player.exe", "", "", ["Other.Player"]);
    Dictionary<uint, ProcessIdentity> processes = new() { [10] = root, [11] = renderer, [20] = other };
    ActiveAudioSession[] active = [new(11, false), new(20, false)];

    Require(AudioSourceMatcher.Match("Player.Music", processes, active).Process == root,
        "Exact window AUMID can match a renderer in the verified process tree.");
    Require(AudioSourceMatcher.Match("player.exe", processes, active).State == MatchState.Ambiguous,
        "Same-named binaries in unrelated installation paths remain ambiguous.");
    Require(AudioSourceMatcher.Match("Player.Music", processes, [new(11, true)]).State == MatchState.Muted,
        "A muted session cannot provide an audible stream.");
    processes[20] = other with { ParentId = root.Id };
    Require(AudioSourceMatcher.Match("Player.Music", processes, active).State == MatchState.Ambiguous,
        "A separately identified renderer beneath the root cannot enter capture.");
    processes[20] = other with { ParentId = root.Id, CreatedAt = 0, ImagePath = "", WindowAppIds = [] };
    Require(AudioSourceMatcher.Match("Player.Music", processes, active).State == MatchState.Ambiguous,
        "An inaccessible active renderer beneath the root cannot enter capture.");
    processes[20] = other;
    processes[10] = root with { CreatedAt = 103 };
    Require(AudioSourceMatcher.Match("Player.Music", processes, active).State == MatchState.Waiting,
        "An old child cannot authenticate against a recycled parent PID.");

    var packaged = root with { PackageFamily = "Test.App_family", AppId = "Test.App_family!Player", WindowAppIds = [] };
    processes[10] = packaged;
    processes[11] = renderer with { PackageFamily = packaged.PackageFamily, ImagePath = @"C:\Player\audio-helper.exe" };
    Require(AudioSourceMatcher.Match(packaged.AppId, processes, active).Process == packaged,
        "An exact package app identity can authenticate its same-package rendering helper.");

    var edge = new ProcessIdentity(30, 0, 103, @"C:\Program Files\Browser\msedge.exe", "", "", ["MSEdge.Profile"]);
    var edgeAudio = edge with { Id = 31, ParentId = 30, CreatedAt = 104, WindowAppIds = [] };
    processes[30] = edge;
    processes[31] = edgeAudio;
    var browserMatch = AudioSourceMatcher.Match("MSEdge.Profile", processes, [new(31, false), new(11, false)],
        _ => throw new InvalidOperationException("Exact identity should not consult Shell registration."));
    Require(browserMatch.Process == edge && browserMatch.Basis == MatchBasis.ExactIdentity,
        "A browser's exact window identity still matches its own rendering tree.");
}

static void CheckRegisteredKuGou()
{
    const string installed = @"R:\自选位置\Music Player\KuGou.exe";
    const string upgraded = @"V:\音乐 软件\KuGou 最新版本\KuGou.exe";
    var main = new ProcessIdentity(100, 0, 10, installed, "", "", []);
    var child = new ProcessIdentity(101, 100, 11, @"R:\自选位置\Music Player\版本 2\Audio Engine.exe", "", "", []);
    Dictionary<uint, ProcessIdentity> processes = new() { [main.Id] = main, [child.Id] = child };
    string[] registeredTargets = [installed, installed.ToUpperInvariant()];
    HashSet<string> signed = new([installed, child.ImagePath, upgraded], StringComparer.OrdinalIgnoreCase);
    var match = Match(new ActiveAudioSession(100, false));
    Require(match.Process == main && match.Basis == MatchBasis.ShellRegistration && match.RegistrationTarget == installed,
        "The same current-user and public target is deduplicated using its full path.");

    match = Match(new ActiveAudioSession(101, false));
    Require(match.Process == child && match.Instance == main && match.RegistrationTarget == installed,
        "A signature-verified renderer in a version directory gets a narrow capture scope.");
    match = Match(new(100, false), new(101, false));
    Require(match.Process == main, "Two authenticated audio PIDs need the smallest shared process tree.");
    Require(Match(new(100, false), new(101, true)).Reason == MatchReason.MutedSession,
        "A process tree containing a muted confirmed audio session cannot be captured.");

    processes.Clear();
    var restarted = main with { Id = 200, ImagePath = upgraded, CreatedAt = 40 };
    processes[restarted.Id] = restarted;
    match = Match(new ActiveAudioSession(200, false));
    Require(match.Process == restarted && match.Basis == MatchBasis.SignedKuGou,
        "Stale registrations permit a new independently verified KuGou installation to match.");
    registeredTargets = [upgraded];
    match = Match(new ActiveAudioSession(200, false));
    Require(match.Process == restarted && match.Basis == MatchBasis.ShellRegistration,
        "Refreshed registration recognizes the new path and process creation time.");

    AudioMatch Match(params ActiveAudioSession[] sessions) =>
        AudioSourceMatcher.Match("kugou", processes, sessions, source =>
        {
            Require(source == "kugou", "Use the media session's app ID when querying Windows registration.");
            return registeredTargets;
        }, process => signed.Contains(process.ImagePath));
}

static void CheckSignedKuGou()
{
    var main = new ProcessIdentity(100, 0, 10, @"X:\酷狗 音乐\KuGou.exe", "", "", []);
    var helper = new ProcessIdentity(101, 100, 11, @"D:\组件\KuGou Audio.exe", "", "", []);
    var external = new ProcessIdentity(200, 0, 12, @"C:\Browser\msedge.exe", "", "", ["MSEdge.1"]);
    Dictionary<uint, ProcessIdentity> processes = new() { [100] = main, [101] = helper, [200] = external };
    HashSet<uint> trusted = [100, 101];

    var match = Match(new(101, false), new(200, false));
    Require(match.Process == helper && match.Instance == main && match.Basis == MatchBasis.SignedKuGou,
        "A proven signed helper and launcher on different volumes establish a verified branch.");
    Require(Match(new(100, false), new(200, false)).Process == main,
        "An unrelated browser stays outside the signed main process tree.");
    Require(Match(new ActiveAudioSession(200, false)).Reason == MatchReason.NoActiveAudio,
        "An active browser cannot fill in for a KuGou session with no active audio.");
    Require(Match(new(100, true), new(200, false)).State == MatchState.Muted,
        "Browser activity cannot override a muted KuGou session.");

    trusted.Remove(100);
    Require(Match(new ActiveAudioSession(100, false)).Reason == MatchReason.UntrustedSignature,
        "A same-name executable with an invalid signature cannot match after trust changes.");
    Require(Match(new ActiveAudioSession(101, false)).Reason == MatchReason.UnverifiedProcessTree,
        "A signed audio helper without a verified entry process cannot take over the source.");

    AudioMatch Match(params ActiveAudioSession[] sessions) =>
        AudioSourceMatcher.Match("KuGou", processes, sessions, _ => [], process => trusted.Contains(process.Id));
}

static void CheckInferredRejections()
{
    var main = new ProcessIdentity(100, 0, 10, @"F:\Player\KuGou.exe", "", "", []);
    var child = new ProcessIdentity(101, 100, 11, @"F:\Player\版本\Audio Engine.exe", "", "", []);
    var foreign = new ProcessIdentity(102, 100, 12, @"F:\Player\版本\KuGou.exe", "", "", ["Other.App"]);
    Dictionary<uint, ProcessIdentity> processes = new() { [100] = main, [101] = child, [102] = foreign };
    string[] targets = [main.ImagePath];
    HashSet<uint> trusted = [100, 101];

    Require(Match(new(100, false), new(102, false)).Reason == MatchReason.UnverifiedProcessTree,
        "A shared directory and active descendant with a matching name cannot enter the main process tree capture.");
    Require(Match(new(101, false), new(102, false)).Process == child,
        "An authorized audio child can be captured without a foreign sibling.");
    Require(Match(new ActiveAudioSession(102, false)).Reason == MatchReason.UnverifiedProcessTree,
        "An untrusted child under a signed registered launcher cannot stand in for the selected source.");

    processes[102] = foreign with { CreatedAt = 0, ImagePath = "" };
    Require(Match(new(100, false), new(102, false)).Reason == MatchReason.UnverifiedProcessTree,
        "Unverified creation time cannot authenticate a child, even when the snapshot suggests a relationship.");
    processes[100] = main with { CreatedAt = 12 };
    Require(Match(new ActiveAudioSession(101, false)).Reason == MatchReason.UnverifiedProcessTree,
        "A recycled parent PID cannot provide the verified chain for a signed renderer.");

    processes[100] = main;
    processes[101] = child with { AppId = "Other.App_family!Player" };
    Require(Match(new ActiveAudioSession(101, false)).Reason == MatchReason.ConflictingIdentity,
        "An explicit conflicting app identity defeats matching paths and signatures.");
    processes[101] = child;

    var second = main with { Id = 200, CreatedAt = 15 };
    processes[200] = second;
    trusted.Add(200);
    Require(Match(new(100, false), new(200, false)).Reason == MatchReason.MultipleInstances,
        "Two active roots with the same known path and signature are ambiguous.");
    Require(Match(new ActiveAudioSession(100, false)).Process == main,
        "An inactive second process does not obscure the sole active instance.");

    var browser = new ProcessIdentity(300, 0, 13, @"C:\Browser\msedge.exe", "", "", ["MSEdge.1"]);
    processes[300] = browser;
    targets = [browser.ImagePath];
    Require(Match(new ActiveAudioSession(300, false)).Reason == MatchReason.UntrustedSignature,
        "A registration pointing to an active browser cannot authenticate it as KuGou.");
    Require(Match(new(300, false), new(100, false)).Reason == MatchReason.ConflictingIdentity,
        "A conflicting live registration cannot resolve to an unrelated signed instance.");

    processes[300] = browser with { WindowAppIds = ["kugou"] };
    targets = [];
    Require(Match(new ActiveAudioSession(300, false)).Reason == MatchReason.UntrustedSignature,
        "A process with a spoofed exact window AUMID cannot match without signed KuGou product evidence.");
    targets = [main.ImagePath];
    var authentic = Match(new(300, false), new(100, false));
    Require(authentic.Process == main && authentic.Basis == MatchBasis.ShellRegistration,
        "A spoofed exact identity cannot suppress a separate registered and trusted instance.");
    processes[100] = main with { WindowAppIds = ["KuGou"] };
    authentic = AudioSourceMatcher.Match("kugou", processes, [new(100, false)],
        _ => throw new InvalidOperationException("A verified exact KuGou identity should not consult registration."),
        process => trusted.Contains(process.Id));
    Require(authentic.Process == processes[100] && authentic.Basis == MatchBasis.ExactIdentity,
        "A trusted exact KuGou identity still takes precedence over Shell registration.");

    AudioMatch Match(params ActiveAudioSession[] sessions) =>
        AudioSourceMatcher.Match("kugou", processes, sessions, _ => targets, process => trusted.Contains(process.Id));
}

static void CheckBands()
{
    var low = MeasureTone(125);
    var high = MeasureTone(4900);
    var lowPeak = Array.IndexOf(low, low.Max());
    var highPeak = Array.IndexOf(high, high.Max());
    Require(lowPeak <= 4 && highPeak >= 11, $"Expected separate frequency bands, got {lowPeak} and {highPeak}.");
    Require(low[lowPeak] > 0.5f && high[highPeak] > 0.5f, "Expected visible FFT magnitudes for both tones.");
    Require(low[highPeak] < 0.05f && high[lowPeak] < 0.05f, "A tone should not energize a distant band.");
}

static float[] MeasureTone(double frequency)
{
    var analyzer = new SpectrumAnalyzer();
    var envelope = new SpectrumEnvelope(analyzer);
    var levels = new float[16];
    var packet = new short[2048];
    long now = Stopwatch.GetTimestamp();
    int windows = 0;
    for (int index = 0; index < 10; index++)
    {
        WriteTone(packet, frequency, index * packet.Length / 2);
        windows += analyzer.Push(MemoryMarshal.AsBytes(packet.AsSpan()), false, false, now);
        envelope.Read(levels, 1, now, true);
        now += Stopwatch.Frequency / 43;
    }

    Require(windows > 2 && analyzer.HasSignal(now), "Real PCM packets must create rolling FFT windows and signal.");
    return levels;
}

static void CheckSilence()
{
    var analyzer = new SpectrumAnalyzer();
    var envelope = new SpectrumEnvelope(analyzer);
    var levels = new float[16];
    var packet = new short[SpectrumAnalyzer.Size * 2];
    WriteTone(packet, 200, 0);
    var bytes = MemoryMarshal.AsBytes(packet.AsSpan());
    long now = Stopwatch.GetTimestamp();
    analyzer.Push(bytes, false, false, now);
    envelope.Read(levels, 1, now, true);

    // Mark the packet silent while it still contains a sine, as a raw WASAPI buffer may.
    long quietAt = now + Stopwatch.Frequency / 10;
    envelope.Read(levels, 1, quietAt, true);
    var initial = levels.Max();
    analyzer.Push(bytes, true, true, quietAt);
    envelope.Read(levels, 1, quietAt, true);
    Require(levels.Max() == initial, "A silent FFT begins a continuous release from the current height.");
    envelope.Read(levels, 1, quietAt + Stopwatch.Frequency / 30, true);
    Require(levels.Max() > 0 && levels.Max() < initial, "Silent packets release existing bars without producing a new signal.");

    long noPacketsAt = now + Stopwatch.Frequency * 2;
    envelope.Read(levels, 1, noPacketsAt, true);
    Require(!analyzer.HasSignal(noPacketsAt) && levels.Max() == 0, "Stale packets ultimately clear the spectrum.");
    Require(!analyzer.TryRead(new float[SpectrumAnalyzer.DetailBands], noPacketsAt), "A packet gap invalidates old spectral data.");

    long allocated = GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < 64; index++)
    {
        noPacketsAt += Stopwatch.Frequency / 10;
        analyzer.Push(bytes, false, false, noPacketsAt);
        envelope.Read(levels, 1, noPacketsAt, true);
    }
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Require(allocatedBytes == 0, $"The steady PCM/FFT and same-size display path allocated {allocatedBytes} bytes.");
}

static void CheckLiveFrameRate()
{
    const int inputWindows = 55;
    long start = Stopwatch.Frequency;
    var analyzer = new SpectrumAnalyzer();
    var slow = new SpectrumEnvelope(analyzer);
    var fast = new SpectrumEnvelope(analyzer);
    var stalled = new SpectrumEnvelope(analyzer);
    float[] levels30 = new float[16], levels60 = new float[16], levelsAfterStall = new float[16];
    var packet = new short[SpectrumAnalyzer.Size * 2];
    var bytes = MemoryMarshal.AsBytes(packet.AsSpan());
    var windowsPushed = 0;
    var sawMotion = false;
    var active = false;
    for (var tick = 0; tick <= 180; tick++)
    {
        long now = start + tick * Stopwatch.Frequency / 60;
        while (windowsPushed < inputWindows)
        {
            var arrivedAt = start + (20 + windowsPushed * 50) * Stopwatch.Frequency / 1000;
            if (arrivedAt > now)
                break;

            WriteTone(packet, windowsPushed % 2 == 0 ? 180 : 1900, windowsPushed * SpectrumAnalyzer.Size);
            Require(analyzer.Push(bytes, silent: windowsPushed >= 20, discontinuity: true, arrivedAt) == 1,
                "Each timed input block supplies one pre-analyzed window.");
            windowsPushed++;
        }

        active = fast.Read(levels60, 1, now, permitSignal: true);
        if (tick % 2 != 0)
            continue;

        Require(slow.Read(levels30, 1, now, permitSignal: true) == active && SameLevels(levels30, levels60),
            $"Frame sampling changes no level, attack, decay or activity at shared timestamp {tick}.");
        sawMotion |= levels60.Max() > 0.3;
        if (tick < 20 || tick >= 38)
        {
            Require(stalled.Read(levelsAfterStall, 1, now, permitSignal: true) == active && SameLevels(levelsAfterStall, levels60),
                $"The envelope resumes at the correct elapsed position after skipped frames at timestamp {tick}.");
        }
    }

    Require(sawMotion && !active && levels60.All(level => level == 0), "The dynamic PCM becomes visible and silence eventually settles.");
    Require(analyzer.WindowSequence == inputWindows, "Reading at multiple display rates does not create FFT windows.");

    static bool SameLevels(float[] first, float[] second) =>
        first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) < 1e-5);
}

static void CheckSettings()
{
    var directory = Directory.CreateTempSubdirectory("Rifflo.CoreChecks-");
    var path = Path.Combine(directory.FullName, "settings.json");
    try
    {
        var store = new SettingsStore(path);
        Require(store.Load() == AppSettings.Defaults, "A missing settings file uses defaults.");
        Require(AppSettings.Defaults.BarCount == 16, "The default spectrum has 16 bands.");
        Require(AppSettings.Defaults.AnimationFrameRate == 30, "New and reset settings use 30 FPS.");
        File.WriteAllText(path, "{ broken json");
        Require(store.Load() == AppSettings.Defaults, "Malformed JSON uses defaults.");
        File.WriteAllText(path, """{"barCount":999,"textColor":"invalid","sensitivity":1000}""");
        var normalized = store.Load();
        Require(normalized.BarCount == 32 && normalized.Sensitivity == 3 && normalized.TextColor == AppSettings.Defaults.TextColor,
            "Valid JSON with invalid values is normalized.");
        var changed = AppSettings.Defaults with
        {
            ShowArtist = false,
            BarCount = 20,
            Position = new(-40, 50) { MonitorId = "Display signifier" },
            PreferredSource = "Player.Music",
            Layout = OverlayLayout.SpectrumLeft,
            TextWidth = 332,
            ScrollMode = CaptionScrollMode.Bounce,
            PositionLocked = true,
            AnimationFrameRate = 60
        };
        Require(store.Save(changed) && new SettingsStore(path).Load() == changed,
            "Saving replaces the existing file and reloads every changed value, including 60 FPS.");
        Require(store.Save(AppSettings.Defaults) && new SettingsStore(path).Load() == AppSettings.Defaults,
            "Saving reset settings persists 30 FPS.");
    }
    finally
    {
        File.Delete(path);
        directory.Delete();
    }
}

static void CheckSettingsCompatibility()
{
    var directory = Directory.CreateTempSubdirectory("Rifflo.SettingsCompatibility-");
    var path = Path.Combine(directory.FullName, "settings.json");
    try
    {
        var store = new SettingsStore(path);
        const string legacy = """
            {
              "showArtist": false, "fontSize": 26, "textColor": "#ABCDEF", "barColor": "#123456",
              "opacity": 0.67, "barCount": 23, "spectrumEnabled": false, "sensitivity": 1.8,
              "position": {"x": -270, "y": 126}, "topmost": false, "preferredSource": "Legacy.Player"
            """;
        var expectedLegacy = new AppSettings
        {
            ShowArtist = false, FontSize = 26, TextColor = "#ABCDEF", BarColor = "#123456",
            Opacity = 0.67, BarCount = 23, SpectrumEnabled = false, Sensitivity = 1.8,
            Position = new(-270, 126), Topmost = false, PreferredSource = "Legacy.Player"
        };
        File.WriteAllText(path, legacy + "}");
        var migrated = store.Load();
        Require(migrated == expectedLegacy && migrated.Layout == OverlayLayout.SpectrumAbove &&
            migrated.TextWidth == 260 && migrated.ScrollMode == CaptionScrollMode.Loop && !migrated.PositionLocked &&
            migrated.AnimationFrameRate == 30,
            "The old position and appearance survive and each new field uses its stated default.");

        const string newFields = """, "Layout": 2, "textWidth": 400, "scrollMode": 1, "positionLocked": true }""";
        var updated = expectedLegacy with
        {
            Layout = OverlayLayout.CaptionLeft, TextWidth = 400, ScrollMode = CaptionScrollMode.Bounce, PositionLocked = true
        };
        var invalidFields = new (string Original, string Invalid, AppSettings Expected)[]
        {
            ("2", "{}", updated with { Layout = OverlayLayout.SpectrumAbove }),
            ("2", "17", updated with { Layout = OverlayLayout.SpectrumAbove }),
            ("400", "[1,2]", updated with { TextWidth = 260 }),
            ("400", "1e999", updated with { TextWidth = 260 }),
            ("400", "900", updated with { TextWidth = 640 }),
            ("400", "10", updated with { TextWidth = 160 }),
            ("1,", "null,", updated with { ScrollMode = CaptionScrollMode.Loop }),
            ("1,", "-1,", updated with { ScrollMode = CaptionScrollMode.Loop }),
            ("true", "\"true\"", updated with { PositionLocked = false })
        };
        foreach (var (original, invalid, expected) in invalidFields)
        {
            File.WriteAllText(path, legacy + newFields.Replace(original, invalid));
            Require(store.Load() == expected, $"Invalid new value {invalid} should repair only its own field.");
        }

        File.WriteAllText(path, legacy.Replace("\"y\": 126", "\"y\": 126, \"monitorId\": [123]") + newFields);
        Require(store.Load() == updated, "Invalid monitor identity preserves the coordinates and the remaining settings.");

        foreach (var invalid in new[] { "0", "90", "-60", "60.5", "2147483648", "1e999", "\"60\"", "true", "null", "[]", "{\"value\":60}" })
        {
            File.WriteAllText(path, legacy + $", \"animationFrameRate\": {invalid}" + newFields);
            Require(store.Load() == updated, $"Invalid frame rate {invalid} preserves all unrelated settings and defaults to 30 FPS.");
        }
    }
    finally
    {
        File.Delete(path);
        directory.Delete();
    }
}

static void CheckBroadcastCaptureSettings()
{
    var directory = Directory.CreateTempSubdirectory("Rifflo.BroadcastCaptureSettings-");
    var path = Path.Combine(directory.FullName, "settings.json");
    try
    {
        var store = new SettingsStore(path);
        var fresh = store.Load();
        Require(!fresh.BroadcastCaptureEnabled && fresh.LiveCaptureEnabled,
            "A new profile starts with broadcast capture off and live spectrum capture on.");

        const string legacy = """
            {
              "position": {"x": -270, "y": 126, "monitorId": "Display signifier"},
              "positionLocked": true, "topmost": false, "layout": 3, "scrollMode": 1, "textWidth": 382,
              "textColor": "#ABCDEF", "barColor": "#123456", "animationFrameRate": 60,
              "liveCaptureEnabled": false, "preferredSource": "Legacy.Player"
            }
            """;
        File.WriteAllText(path, legacy);
        var savedPreferences = AppSettings.Defaults with
        {
            BroadcastCaptureEnabled = false,
            Position = new(-270, 126) { MonitorId = "Display signifier" },
            PositionLocked = true, Topmost = false, Layout = OverlayLayout.SpectrumLeft,
            ScrollMode = CaptionScrollMode.Bounce, TextWidth = 382, TextColor = "#ABCDEF", BarColor = "#123456",
            AnimationFrameRate = 60, LiveCaptureEnabled = false, PreferredSource = "Legacy.Player"
        };
        Require(store.Load() == savedPreferences,
            "A profile written before broadcast capture keeps its position, lock, topmost, layout, colors, frame rate and player.");

        foreach (var broadcastEnabled in new[] { true, false })
        foreach (var liveEnabled in new[] { false, true })
        {
            var expected = savedPreferences with { BroadcastCaptureEnabled = broadcastEnabled, LiveCaptureEnabled = liveEnabled };
            Require(store.Save(expected) && new SettingsStore(path).Load() == expected,
                $"Broadcast capture {broadcastEnabled} and live spectrum {liveEnabled} must survive reload independently.");
        }
    }
    finally
    {
        File.Delete(path);
        directory.Delete();
    }
}

static void WriteTone(Span<short> packet, double frequency, int offset)
{
    for (int index = 0; index < packet.Length / 2; index++)
    {
        var sample = (short)(Math.Sin((offset + index) * Math.Tau * frequency / SpectrumAnalyzer.SampleRate) * 10000);
        packet[index * 2] = sample;
        packet[index * 2 + 1] = (short)-sample;
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
