using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rifflo.Audio;
using Rifflo.Media;

internal static class AudioMonitorChecks
{
    private static readonly AudioTiming Timing = new(360, 360, 360, 520, 70, 80, 140, 410, 10);
    private static readonly ProcessIdentity First = new(101, 0, 12, @"C:\players\first.exe", "", "", []);
    private static readonly ProcessIdentity Other = new(202, 0, 18, @"C:\players\other.exe", "", "", []);

    public static async Task PacketHealth()
    {
        var fixture = new Fixture();
        await using var monitor = fixture.CreateMonitor();
        var ready = monitor.SelectAsync("First", CancellationToken.None);
        await WaitFor(() => monitor.CaptureStatus.Recording);
        var recorder = fixture.LastRecorder;
        Check(monitor.CaptureStatus.State == AudioCaptureState.Connecting && !ready.IsCompleted,
            "An open recorder awaits its first data packet.");

        recorder.Emit(new byte[4096 * 4], silent: true);
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        Check(monitor.CaptureStatus.State == AudioCaptureState.Live && !monitor.Capturing,
            "A silent WASAPI packet makes the connection healthy independently of audible signal.");

        int ownedCaptures = fixture.Created;
        var generation = monitor.CaptureStatus.Generation;
        await monitor.SelectAsync("FIRST", CancellationToken.None);
        Check(monitor.CaptureStatus.Generation == generation && fixture.Created == ownedCaptures,
            "Selecting the same source reuses the healthy capture.");

        var nearSilence = new byte[4096 * 4];
        for (int offset = 0; offset < nearSilence.Length; offset += 4)
            nearSilence[offset] = 1;

        long silentUntil = Environment.TickCount64 + Timing.PacketGrace + Timing.CheckInterval * 10;
        while (Environment.TickCount64 < silentUntil)
        {
            recorder.Emit(nearSilence);
            Check(monitor.CaptureStatus.State == AudioCaptureState.Live, "Continual low-level packets keep the real spectrum.");
            await Task.Delay(15);
        }

        var bands = new float[AudioMonitor.BandCount];
        monitor.ReadBands(bands, 1);
        Check(bands.All(level => level == 0) && !monitor.Capturing && monitor.PacketCount > 1 && monitor.FftCount > 0,
            "Sub-threshold signal still counts as healthy audio packets and keeps the bars low.");
        Check(fixture.Created == ownedCaptures, "Identity checks reuse the connected recorder during low-level audio.");
    }

    public static async Task NoPacketsAndRecovery()
    {
        var fixture = new Fixture();
        await using var monitor = fixture.CreateMonitor();
        var ready = monitor.SelectAsync("First", CancellationToken.None);
        await WaitFor(() => monitor.CaptureStatus.Recording);
        var initial = fixture.LastRecorder;
        var initialStart = initial.StartedAt;
        initial.Emit([]);
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        Check(monitor.CaptureStatus is { State: AudioCaptureState.Fallback, Reason: AudioCaptureReason.NoPackets, Recording: false } &&
            monitor.PacketCount == 0 && Environment.TickCount64 >= initialStart + Timing.FirstPacketGrace,
            "Empty callbacks do not establish data flow and fallback waits for the first-packet grace.");
        await initial.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Created == 1, "The recorder is released before rate-limited retry.");

        await WaitFor(() => monitor.CaptureStatus.Recording && fixture.Created == 2);
        Check(monitor.CaptureStatus.State == AudioCaptureState.Fallback,
            "Retry keeps the fallback display until a packet actually arrives.");
        var recovered = fixture.LastRecorder;
        recovered.Emit(new byte[4096 * 4], silent: true);
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Live);
        Check(!monitor.Capturing, "A silent packet recovers the real quiet display.");

        await Task.Delay(Timing.PacketGrace / 3);
        Check(monitor.CaptureStatus.State == AudioCaptureState.Live, "A short callback gap preserves the live connection.");
        await WaitFor(() => monitor.CaptureStatus is { State: AudioCaptureState.Fallback, Recording: false });
        Check(monitor.CaptureStatus.Reason == AudioCaptureReason.NoPackets, "Sustained packet loss triggers fallback after a live connection.");
        await recovered.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitFor(() => monitor.CaptureStatus.Recording && fixture.Created == 3);
        fixture.LastRecorder.Emit(new byte[4096 * 4], silent: true);
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Live);
    }

    public static async Task MutingAndTopology()
    {
        var fixture = new Fixture { Match = _ => new(MatchState.Muted, MatchBasis.ExactIdentity, MatchReason.MutedSession) };
        await using var monitor = fixture.CreateMonitor();
        await monitor.SelectAsync("First", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check(monitor.CaptureStatus is { State: AudioCaptureState.Muted, Recording: false } && fixture.Created == 0,
            "An explicitly muted source is quiet with no recorder and no fallback.");

        fixture.Match = _ => Matched(First);
        fixture.LastSource.Changed();
        await WaitFor(() => monitor.CaptureStatus.Recording);
        var waiting = fixture.LastRecorder;
        fixture.Match = _ => new(MatchState.Muted, MatchBasis.ExactIdentity, MatchReason.MutedSession);
        fixture.LastSource.Changed();
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Muted);
        await waiting.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(Timing.FirstPacketGrace);
        Check(monitor.CaptureStatus is { State: AudioCaptureState.Muted, Reason: AudioCaptureReason.None } &&
            monitor.PacketCount == 0, "Explicit mute supersedes the first-packet deadline and stays quiet without callbacks.");

        fixture.Match = _ => Matched(First);
        fixture.LastSource.Changed();
        await WaitFor(() => monitor.CaptureStatus.Recording);
        var beforeMute = fixture.LastRecorder;
        beforeMute.Emit(new byte[4096 * 4], silent: true);
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Live);

        fixture.Match = _ => new(MatchState.Muted, MatchBasis.ExactIdentity, MatchReason.MutedSession);
        fixture.LastSource.Changed();
        var count = monitor.PacketCount;
        beforeMute.Emit(new byte[4096 * 4], silent: true);
        Check(monitor.PacketCount == count, "Evidence changes supersede approval before a new match finishes.");
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Muted);
        await beforeMute.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!monitor.CaptureStatus.Recording, "Muting releases the process capture.");

        fixture.Match = _ => Matched(First);
        fixture.LastSource.Changed();
        await WaitFor(() => monitor.CaptureStatus.Recording);
        var beforeDeviceChange = fixture.LastRecorder;
        beforeDeviceChange.Emit(new byte[4096 * 4], silent: true);
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Live);

        fixture.LastSource.TopologyChanged();
        await beforeDeviceChange.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitFor(() => monitor.CaptureStatus is { State: AudioCaptureState.Connecting, Recording: true });
        Check(fixture.Created == 4, "Output topology change reconnects even when the target process remains the same.");
        fixture.LastRecorder.Emit(new byte[4096 * 4], silent: true);
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Live);
    }

    public static async Task FailedMatchAndCapture()
    {
        var fixture = new Fixture { Match = _ => new(MatchState.Ambiguous, MatchBasis.ExactIdentity, MatchReason.MultipleInstances) };
        await using var monitor = fixture.CreateMonitor();
        await monitor.SelectAsync("First", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check(monitor.CaptureStatus is { State: AudioCaptureState.Fallback, Reason: AudioCaptureReason.AmbiguousSource } &&
            monitor.CaptureAttemptCount == 0, "Ambiguity falls back without opening any capture.");

        fixture.Open = (_, _) => throw new InvalidOperationException("Injected recorder failure.");
        fixture.Match = _ => Matched(First);
        fixture.LastSource.Changed();
        await WaitFor(() => monitor.CaptureStatus.Reason == AudioCaptureReason.CaptureFailed);
        Check(monitor.CaptureAttemptCount == 1 && !monitor.CaptureStatus.Recording, "Explicit recorder failure is observable and backs off.");

        fixture.Open = null;
        await WaitFor(() => monitor.CaptureStatus.Recording);
        fixture.LastRecorder.Emit(new byte[4096 * 4], silent: true);
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Live);

        fixture.LastRecorder.Fail();
        await WaitFor(() => monitor.CaptureStatus is
            { State: AudioCaptureState.Fallback, Reason: AudioCaptureReason.CaptureFailed, Recording: false });
    }

    public static async Task BlockedMatchAndReplacement()
    {
        using var releaseOld = new ManualResetEventSlim();
        var fixture = new Fixture
        {
            Match = source =>
            {
                if (source == "Blocked")
                {
                    releaseOld.Wait();
                    return Matched(First);
                }
                return Matched(Other);
            }
        };
        await using var monitor = fixture.CreateMonitor();
        try
        {
            var ready = monitor.SelectAsync("Blocked", CancellationToken.None);
            await WaitFor(() => monitor.MatchAttemptCount == 1);
            var blocked = fixture.LastSource;
            await ready.WaitAsync(TimeSpan.FromSeconds(5));
            Check(monitor.CaptureStatus is { State: AudioCaptureState.Fallback, Reason: AudioCaptureReason.MatchTimeout },
                "Initial matching times out even when the native operation never returns.");
            await WaitFor(() => monitor.MatchAttemptCount == 2);
            await Task.Delay(Timing.MatchGrace + Timing.RetryDelay(2) + Timing.CheckInterval * 5);
            Check(monitor.MatchAttemptCount == 2 && monitor.CaptureAttemptCount == 0,
                "One independent retry is allowed, with a bound on simultaneous stuck operations.");

            var replacement = monitor.SelectAsync("Other", CancellationToken.None);
            await WaitFor(() => monitor.CaptureStatus is { Source: "Other", Recording: true });
            var current = fixture.LastRecorder;
            current.Emit(new byte[4096 * 4], silent: true);
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));

            releaseOld.Set();
            await blocked.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(monitor.CaptureStatus is { State: AudioCaptureState.Live, Source: "Other" } &&
                fixture.Created == 1 && current.Target == Other,
                "Late matching from an earlier generation cannot override the new source or open a recorder.");
        }
        finally { releaseOld.Set(); }
    }

    public static async Task BlockedMatchSameSource()
    {
        using var releaseOld = new ManualResetEventSlim();
        int inspections = 0;
        var fixture = new Fixture
        {
            Match = _ =>
            {
                if (Interlocked.Increment(ref inspections) == 1)
                {
                    releaseOld.Wait();
                    return Matched(Other);
                }
                return Matched(First);
            }
        };
        await using var monitor = fixture.CreateMonitor();
        try
        {
            var ready = monitor.SelectAsync("First", CancellationToken.None);
            await WaitFor(() => monitor.MatchAttemptCount == 1);
            var blocked = fixture.LastSource;
            var generation = monitor.CaptureStatus.Generation;
            await ready.WaitAsync(TimeSpan.FromSeconds(5));
            Check(monitor.CaptureStatus is { State: AudioCaptureState.Fallback, Reason: AudioCaptureReason.MatchTimeout, Recording: false },
                "The first failed match publishes a timeout before retrying.");

            await WaitFor(() => monitor.CaptureStatus is { State: AudioCaptureState.Fallback, Recording: true });
            var recorder = fixture.LastRecorder;
            recorder.Emit(new byte[4096 * 4], silent: true);
            await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Live);
            Check(monitor.MatchAttemptCount == 2 && monitor.CaptureStatus.Generation == generation &&
                recorder.Target == First && !blocked.Disposed.Task.IsCompleted,
                "A retry restores the original selection on a silent packet while its first lookup remains blocked.");

            monitor.Stop();
            releaseOld.Set();
            await Task.WhenAll(blocked.Disposed.Task, recorder.Disposed.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Check(monitor.CaptureStatus is { State: AudioCaptureState.Idle, Recording: false } &&
                monitor.CaptureAttemptCount == 1 && fixture.Created == 1,
                "A matcher returning after manual shutdown cannot open or publish any recorder.");
        }
        finally { releaseOld.Set(); }
    }

    public static async Task BlockedActivation()
    {
        var stale = new FakeRecorder(First);
        var held = new TaskCompletionSource<IAudioRecorder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture { Open = (_, _) => held.Task };
        await using var monitor = fixture.CreateMonitor();
        var ready = monitor.SelectAsync("First", CancellationToken.None);
        await WaitFor(() => monitor.CaptureAttemptCount == 1);
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        Check(monitor.CaptureStatus is { State: AudioCaptureState.Fallback, Reason: AudioCaptureReason.CaptureFailed, Recording: false },
            "A blocked activation has a bounded wait and falls back.");
        await Task.Delay(Timing.RetryDelay(1) + Timing.CheckInterval * 5);
        Check(monitor.CaptureAttemptCount == 1, "A timed-out activation does not multiply pending recorder creation.");

        fixture.Open = null;
        held.TrySetResult(stale);
        await stale.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(stale.StartedAt == 0, "A recorder returned after activation timed out is disposed without starting.");
        await WaitFor(() => monitor.CaptureStatus.Recording);
        fixture.LastRecorder.Emit(new byte[4096 * 4], silent: true);
        await WaitFor(() => monitor.CaptureStatus.State == AudioCaptureState.Live);
    }

    public static async Task CancellationAndLatePackets()
    {
        var stale = new FakeRecorder(First);
        var held = new TaskCompletionSource<IAudioRecorder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture
        {
            Match = source => Matched(source == "First" ? First : Other),
            Open = (target, _) => target == First ? held.Task : Task.FromResult<IAudioRecorder?>(new FakeRecorder(target))
        };
        await using var monitor = fixture.CreateMonitor();
        using var request = new CancellationTokenSource();
        var ready = monitor.SelectAsync("First", request.Token);
        await WaitFor(() => monitor.CaptureAttemptCount == 1);
        request.Cancel();
        Check(ready.IsCanceled && monitor.CaptureStatus.State == AudioCaptureState.Idle, "Cancellation clears state and the pending selection immediately.");

        fixture.Open = null;
        var replacement = monitor.SelectAsync("Other", CancellationToken.None);
        await WaitFor(() => monitor.CaptureStatus is { Source: "Other", Recording: true });
        var recorder = fixture.LastRecorder;
        var copiedCallback = recorder.CaptureCallback();
        var samples = new byte[4096 * 4];
        recorder.Emit(samples);
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));

        held.TrySetResult(stale);
        await stale.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(stale.StartedAt == 0 && monitor.CaptureStatus.State == AudioCaptureState.Live,
            "Canceled native activation cannot start capturing after selection changes.");

        monitor.Stop();
        var packets = monitor.PacketCount;
        var ffts = monitor.FftCount;
        var attempts = monitor.MatchAttemptCount;
        await recorder.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        copiedCallback?.Invoke(samples, false, false, Stopwatch.GetTimestamp());
        var bands = Enumerable.Repeat(1f, AudioMonitor.BandCount).ToArray();
        monitor.ReadBands(bands, 1);
        await Task.Delay(Timing.RetryDelay(1) + Timing.CheckInterval * 5);
        Check(monitor.CaptureStatus.State == AudioCaptureState.Idle && !monitor.CaptureStatus.Recording &&
            monitor.PacketCount == packets && monitor.FftCount == ffts && monitor.MatchAttemptCount == attempts &&
            bands.All(level => level == 0), "Stopping rejects late packets and suppresses retry, FFTs, and visible bands.");
    }

    public static async Task CompetitionChanges()
    {
        var fixture = new Fixture { MatchCompetition = AudioOwnershipChecks.BrowserMatch };
        await using var monitor = fixture.CreateMonitor();
        var playing = new TrackState("Edge App Window", "Example", "", PlayState.Playing);
        monitor.ObserveMediaSelection(playing);
        var selected = monitor.SelectAsync("Edge App Window", AudioCompetition.Empty, CancellationToken.None);
        await WaitFor(() => monitor.CaptureStatus.Recording);
        var original = fixture.LastRecorder;
        var oldCallback = original.CaptureCallback();
        var silent = new byte[4096 * 4];
        original.Emit(silent, silent: true);
        await selected.WaitAsync(TimeSpan.FromSeconds(5));
        long generation = monitor.CaptureStatus.Generation;

        var competitors = AudioCompetition.FromSources(["MSEdge"]);
        monitor.ObserveMediaSelection(playing with { AudioCompetition = competitors });
        Check(monitor.CaptureStatus.State == AudioCaptureState.Idle,
            "The existing live result is revoked at media observation, before the controller starts a new request.");
        var packets = monitor.PacketCount;
        var attempts = monitor.MatchAttemptCount;
        oldCallback?.Invoke(silent, false, false, Stopwatch.GetTimestamp());
        await monitor.SelectAsync("Edge App Window", AudioCompetition.Empty, CancellationToken.None);
        Check(monitor.CaptureStatus.State == AudioCaptureState.Idle && monitor.MatchAttemptCount == attempts &&
            monitor.PacketCount == packets, "An outdated controller request and old packet cannot restore stale approval.");

        var blocked = monitor.SelectAsync("Edge App Window", competitors, CancellationToken.None);
        Check(monitor.CaptureStatus.Generation > generation && monitor.CaptureStatus.State != AudioCaptureState.Live,
            "The new ownership evidence creates an independent selection.");
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        await original.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(monitor.PacketCount == packets && fixture.Created == 1 && monitor.CaptureStatus is
            { State: AudioCaptureState.Fallback, Reason: AudioCaptureReason.AmbiguousSource, Recording: false },
            "The shared browser tree stays unapproved and publishes no old or new audio.");

        var blockedGeneration = monitor.CaptureStatus.Generation;
        await monitor.SelectAsync("EDGE APP WINDOW", AudioCompetition.FromSources(["msedge"]), CancellationToken.None);
        Check(monitor.CaptureStatus.Generation == blockedGeneration, "The equivalent active identity set does not create another request.");

        monitor.ObserveMediaSelection(playing);
        var recovery = monitor.SelectAsync("Edge App Window", AudioCompetition.Empty, CancellationToken.None);
        await WaitFor(() => monitor.CaptureStatus.Recording && fixture.Created == 2);
        fixture.LastRecorder.Emit(silent, silent: true);
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Check(monitor.CaptureStatus is { State: AudioCaptureState.Live, Source: "Edge App Window" } &&
            monitor.CaptureStatus.Generation > blockedGeneration, "When the competitor exits, a silent packet restores the original source.");

        monitor.Stop();
        attempts = monitor.MatchAttemptCount;
        monitor.ObserveMediaSelection(playing with { AudioCompetition = competitors });
        monitor.ObserveMediaSelection(playing);
        Check(monitor.CaptureStatus.State == AudioCaptureState.Idle && monitor.MatchAttemptCount == attempts,
            "Media updates without a new controller request never restart stopped capture.");

        var held = new TaskCompletionSource<IAudioRecorder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new FakeRecorder(First);
        fixture.Open = (_, _) => held.Task;
        var activationAttempts = monitor.CaptureAttemptCount;
        var pending = monitor.SelectAsync("Edge App Window", AudioCompetition.Empty, CancellationToken.None);
        await WaitFor(() => monitor.CaptureAttemptCount > activationAttempts);
        monitor.ObserveMediaSelection(playing with { AudioCompetition = competitors });
        held.TrySetResult(late);
        await late.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(pending.IsCanceled && late.StartedAt == 0 && !monitor.CaptureStatus.Recording,
            "A competing media identity revokes prior approval even if an unbound recorder returns later.");
    }

    public static async Task SameSessionTransition()
    {
        var fixture = new Fixture();
        await using var monitor = fixture.CreateMonitor();
        var events = new ConcurrentQueue<AudioCaptureEvent>();
        monitor.Trace += events.Enqueue;
        var playing = new TrackState("First", "Old track", "", PlayState.Playing) { SessionId = 1 };
        var recorder = await Play(fixture, monitor, playing, Tone(115, SpectrumAnalyzer.Size + 1000));
        var levels = new float[AudioMonitor.BandCount];
        monitor.ReadBands(levels, 1, Stopwatch.GetTimestamp(), out var sampleGeneration);
        monitor.BandsSubmitted(sampleGeneration);
        var peakBefore = levels.Max();
        Check(peakBefore > 0.5f, "The original low tone has a visible spectrum.");
        var opened = events.Single(evt => evt.Kind == "AudioNativeOpened");
        var low = events.Single(evt => evt.Kind == "AudioFirstFft");
        var queuedCallback = recorder.CaptureCallback()!;
        var packets = monitor.PacketCount;
        var windows = monitor.FftCount;

        var next = playing with { Title = "New track" };
        var at = Stopwatch.GetTimestamp();
        var stopped = next with { IsTransition = true, State = PlayState.Stopped, ObservedAt = at };
        monitor.ObserveMediaSelection(stopped);
        await monitor.SelectAsync(playing.Source, CancellationToken.None);
        recorder.Emit(Tone(115, SpectrumAnalyzer.Size));
        Check(!monitor.Capturing && monitor.CaptureStatus is { State: AudioCaptureState.Live, Recording: true } &&
            !recorder.Disposed.Task.IsCompleted && monitor.PacketCount == packets && monitor.FftCount == windows,
            "The media worker closes the sample gate but retains the healthy native recorder.");

        monitor.ReadBands(levels, 1, at + Stopwatch.Frequency / 30, out sampleGeneration);
        var afterOneFrame = levels.Max();
        monitor.ReadBands(levels, 1, at + Stopwatch.Frequency / 15, out sampleGeneration);
        Check(sampleGeneration == 0 && afterOneFrame < peakBefore && levels.Max() < afterOneFrame && levels.Max() > 0,
            "During the gate all visual levels only decay as time advances, without old FFT history.");

        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        fixture.Match = _ =>
        {
            entered.Set();
            unblock.Wait(TimeSpan.FromSeconds(2));
            return Matched(First);
        };
        try
        {
            var playingAt = Stopwatch.GetTimestamp();
            monitor.ObserveMediaSelection(next with { ObservedAt = playingAt });
            await WaitFor(() => entered.IsSet);
            recorder.Emit(Tone(4500, SpectrumAnalyzer.Size));
            Check(monitor.PacketCount == packets, "A pending resume match cannot admit packets.");
            unblock.Set();
            await WaitFor(() => events.Any(evt => evt.Kind == "AudioResumeVerified"));

            queuedCallback(Tone(115, SpectrumAnalyzer.Size), false, false, at - 1);
            queuedCallback(Tone(115, SpectrumAnalyzer.Size), false, false, 0);
            Check(monitor.PacketCount == packets && monitor.FftCount == windows,
                "Callbacks delivered after resume can't apply queued pre-resume or untimestamped packets.");
            recorder.Emit(Tone(4500, SpectrumAnalyzer.Size - 1));
            Check(monitor.FftCount == windows, "No old partial FFT window is left to complete.");
            recorder.Emit(Tone(4500, 1));
            Check(monitor.FftCount == windows + 1, "A full new FFT window is usable as soon as it arrives.");
            monitor.ReadBands(levels, 1, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10, out sampleGeneration);
            monitor.BandsSubmitted(sampleGeneration);

            var firstNewFft = events.Last(evt => evt.Kind == "AudioFirstFft");
            var newBars = events.Last(evt => evt.Kind == "AudioFirstBands");
            Check(fixture.Created == 1 && events.Count(evt => evt.Kind == "AudioNativeOpened") == 1 &&
                firstNewFft.ConnectionId == opened.ConnectionId && firstNewFft.SampleGeneration != opened.SampleGeneration &&
                firstNewFft.PeakBand > low.PeakBand + 35 && newBars.SampleGeneration == firstNewFft.SampleGeneration &&
                levels.Skip(AudioMonitor.BandCount / 2).Max() > levels.Take(AudioMonitor.BandCount / 2).Max(),
                "The same connection delivers a new high-frequency spectrum and corresponding bars in a fresh sample generation.");

            monitor.ObserveMediaSelection(stopped with { ObservedAt = Stopwatch.GetTimestamp() });
            await WaitFor(() => events.Count(evt => evt.Kind == "AudioSuspended") == 2);
            monitor.ObserveMediaSelection(next with { ObservedAt = Stopwatch.GetTimestamp() });
            await WaitFor(() => events.Count(evt => evt.Kind == "AudioResumeVerified") == 2);
            recorder.Emit(Tone(115, SpectrumAnalyzer.Size), silent: true);
            monitor.ReadBands(levels, 1, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2, out _);
            Check(levels.All(level => level == 0) && fixture.Created == 1,
                "A silent second transition retains the recorder and naturally settles to zero.");
        }
        finally { unblock.Set(); }
    }

    public static async Task TransitionRevocation()
    {
        await Revoked(async (_, monitor, _, stopped) =>
        {
            var startedAt = stopped.ObservedAt;
            for (int repeat = 0; repeat < 5; repeat++)
            {
                await Task.Delay(85);
                monitor.ObserveMediaSelection(stopped with { ObservedAt = Stopwatch.GetTimestamp() });
            }
            Check(!monitor.CaptureStatus.Recording && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= 300,
                "Repeated temporary events cannot extend the recorder deadline.");
        });
        await Revoked((_, monitor, playing, _) =>
        {
            monitor.ObserveMediaSelection(playing with { State = PlayState.Paused });
            Check(!monitor.CaptureStatus.Recording, "Explicit pause releases the capture synchronously.");
            return Task.CompletedTask;
        });
        await Revoked((_, monitor, _, _) =>
        {
            monitor.ObserveMediaSelection(TrackState.Empty);
            Check(!monitor.CaptureStatus.Recording, "Explicit session removal releases the capture synchronously.");
            return Task.CompletedTask;
        });
        await Revoked(async (_, monitor, playing, _) =>
        {
            monitor.ObserveMediaSelection(playing with { SessionId = 2 });
            Check(!monitor.CaptureStatus.Recording, "Another session cannot inherit a recorder even under the same source name.");
            var matches = monitor.MatchAttemptCount;
            await monitor.SelectAsync(playing.Source, playing.AudioCompetition, CancellationToken.None, playing.SessionId);
            Check(monitor.CaptureStatus.State == AudioCaptureState.Idle && monitor.MatchAttemptCount == matches,
                "An outdated UI request cannot start a newer session's recorder under the old session ID.");
        });
        await Revoked((_, monitor, playing, stopped) =>
        {
            monitor.ObserveMediaSelection(stopped with { AudioCompetition = AudioCompetition.FromSources(["Competitor"]) });
            Check(!monitor.CaptureStatus.Recording, "Changed media competition immediately invalidates the retained ownership.");
            monitor.ObserveMediaSelection(playing);
            return Task.CompletedTask;
        });
        await Revoked((fixture, monitor, _, _) =>
        {
            fixture.LastSource.TopologyChanged();
            Check(!monitor.CaptureStatus.Recording, "An endpoint notification immediately releases the retained recorder.");
            return Task.CompletedTask;
        });
        await Revoked((fixture, monitor, _, _) =>
        {
            fixture.LastRecorder.Fail();
            Check(!monitor.CaptureStatus.Recording, "Native recorder termination immediately releases the retained recorder.");
            return Task.CompletedTask;
        });
        await Revoked((_, monitor, playing, _) =>
        {
            monitor.Stop();
            monitor.ObserveMediaSelection(playing);
            return Task.CompletedTask;
        });
        await Revoked((_, monitor, _, _) => monitor.DisposeAsync().AsTask());
        await Revoked((fixture, monitor, playing, _) =>
        {
            fixture.Match = _ => new(MatchState.Ambiguous, MatchBasis.ExactIdentity, MatchReason.MultipleInstances);
            monitor.ObserveMediaSelection(playing with { ObservedAt = Stopwatch.GetTimestamp() });
            return Task.CompletedTask;
        });
        await Revoked((fixture, monitor, playing, _) =>
        {
            fixture.Match = _ => Matched(First with { CreatedAt = First.CreatedAt + 1 });
            monitor.ObserveMediaSelection(playing with { ObservedAt = Stopwatch.GetTimestamp() });
            return Task.CompletedTask;
        });
        await Revoked((fixture, monitor, playing, _) =>
        {
            fixture.Match = _ => Matched(First) with { EndpointIds = ["different-output"] };
            monitor.ObserveMediaSelection(playing with { ObservedAt = Stopwatch.GetTimestamp() });
            return Task.CompletedTask;
        });

        static async Task Revoked(Func<Fixture, AudioMonitor, TrackState, TrackState, Task> revoke)
        {
            var fixture = new Fixture();
            await using var monitor = fixture.CreateMonitor();
            var playing = new TrackState("First", "Track", "", PlayState.Playing) { SessionId = 1 };
            var recorder = await Play(fixture, monitor, playing, Tone(115, SpectrumAnalyzer.Size));
            var stopped = playing with { IsTransition = true, State = PlayState.Stopped, ObservedAt = Stopwatch.GetTimestamp() };
            monitor.ObserveMediaSelection(stopped);
            var packets = monitor.PacketCount;
            var lateCallback = recorder.CaptureCallback()!;
            await revoke(fixture, monitor, playing, stopped);
            await recorder.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            lateCallback(Tone(115, SpectrumAnalyzer.Size), false, false, Stopwatch.GetTimestamp());
            Check(monitor.PacketCount == packets && fixture.Created == 1 && monitor.CaptureStatus.State == AudioCaptureState.Idle,
                "No late packet or observation can revive a retired transition request.");
        }
    }

    private static async Task<FakeRecorder> Play(Fixture fixture, AudioMonitor monitor, TrackState track, byte[] initialSamples)
    {
        monitor.ObserveMediaSelection(track);
        var ready = monitor.SelectAsync(track.Source, CancellationToken.None);
        await WaitFor(() => monitor.CaptureStatus.Recording);
        var recorder = fixture.LastRecorder;
        recorder.Emit(initialSamples);
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        return recorder;
    }

    private static byte[] Tone(int frequency, int frames)
    {
        var bytes = new byte[frames * 4];
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        for (int frame = 0; frame < frames; frame++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * frequency * frame / SpectrumAnalyzer.SampleRate) * 12000);
            samples[frame * 2] = samples[frame * 2 + 1] = value;
        }
        return bytes;
    }

    private static AudioMatch Matched(ProcessIdentity target) => new(MatchState.Matched, MatchBasis.ExactIdentity,
        MatchReason.Matched, target, EndpointIds: ["output"]);

    private static async Task WaitFor(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        Check(condition(), $"The expected monitor state did not arrive within five seconds: {description}");
    }

    private static void Check(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture
    {
        private int created;
        private FakeRecorder? recorder;
        private FakeSource? activeSource;
        public Func<string, AudioMatch> Match { get; set; } = _ => Matched(First);
        public Func<AudioCompetition, AudioMatch>? MatchCompetition { get; set; }
        public Func<ProcessIdentity, CancellationToken, Task<IAudioRecorder?>>? Open { get; set; }
        public int Created => Volatile.Read(ref created);
        public FakeRecorder LastRecorder => Volatile.Read(ref recorder)!;
        public FakeSource LastSource => Volatile.Read(ref activeSource)!;

        public AudioMonitor CreateMonitor() => new((changed, topologyChanged) =>
        {
            var source = new FakeSource(this, changed, topologyChanged);
            Volatile.Write(ref activeSource, source);
            return source;
        }, (target, token) =>
        {
            if (Open is { } open)
                return open(target, token);
            var opened = new FakeRecorder(target);
            Volatile.Write(ref recorder, opened);
            Interlocked.Increment(ref created);
            return Task.FromResult<IAudioRecorder?>(opened);
        }, Timing);
    }

    private sealed class FakeSource(Fixture owner, Action changed, Action topologyChanged) : IAudioSource
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AudioObservation Inspect(string source, AudioCompetition competition, int topology, bool includeLevels, CancellationToken token)
            => new(owner.MatchCompetition?.Invoke(competition) ?? owner.Match(source), []);
        public void Changed() => changed();
        public void TopologyChanged() => topologyChanged();
        public void Dispose() => Disposed.TrySetResult();
    }

    private sealed class FakeRecorder(ProcessIdentity target) : IAudioRecorder
    {
        public event AudioPacket? Packet;
        public event Action? Stopped;
        public ProcessIdentity Target { get; } = target;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long StartedAt { get; private set; }
        public bool IsAlive => !Disposed.Task.IsCompleted;
        public void Start() => StartedAt = Environment.TickCount64;
        public void Emit(ReadOnlySpan<byte> data, bool silent = false) => Packet?.Invoke(data, silent, false, Stopwatch.GetTimestamp());
        public AudioPacket? CaptureCallback() => Packet;
        public void Fail() => Stopped?.Invoke();
        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
