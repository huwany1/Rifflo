using System.Diagnostics;
using System.Globalization;
using System.IO;
using Rifflo.Audio;
using Rifflo.Media;
using Rifflo.Overlay;
using Rifflo.Settings;

internal static class PlaybackTransitionChecks
{
    internal static void Run()
    {
        ReplayHistoricalSwitches();
        CheckMetadataAndFastSwitches();
        CheckDeadlines();
        CheckQueuedTimeouts();
        CheckRemovalAndHandoff();
        CheckPauseAndUserControls();
        Console.WriteLine("Playback transition checks passed: historical KuGou next and previous, untitled and rapid updates, 299/300/301 ms at 30/60 FPS, pause, handoff, and user controls.");
    }

    private static void ReplayHistoricalSwitches()
    {
        // Selection rows transcribed verbatim from artifacts/verification/video-316d8225/kugou-switch-events.tsv.
        var path = Path.Combine(AppContext.BaseDirectory, "KuGouSwitch.tsv");
        foreach (var settingsVisible in new[] { false, true })
        {
            var playback = new PlaybackTransition();
            foreach (var row in File.ReadLines(path))
            {
                var columns = row.Split('\t');
                var observedAt = At(double.Parse(columns[0], CultureInfo.InvariantCulture));
                var fields = columns[3].Split(' ', 4);
                var state = fields[1] == "None" ? PlayState.Changing : Enum.Parse<PlayState>(fields[1]);
                var title = fields[2]["title=".Length..];
                Require(playback.Expire(observedAt) is null, $"{columns[1]}: the real sequence finishes inside the deadline");
                var selected = playback.Observe(Track(state, title), observedAt);
                var visibility = OverlayPlayback.Resolve(selected, pauseTail: false, manuallyHidden: false, settingsVisible);
                Require(visibility.ShowReal && !visibility.ShowPreview, $"{columns[1]}: the real caption persists with settingsVisible={settingsVisible}");
                Require(selected.Title == title, $"{columns[1]}: metadata received before Stopped never rolls back");
                Require(selected.IsTransition == (state != PlayState.Playing), $"{columns[1]}: Playing completes the switch on arrival");
            }
        }
    }

    private static void CheckMetadataAndFastSwitches()
    {
        var playback = new PlaybackTransition();
        var first = Track(PlayState.Playing, "First") with { Artist = "Singer A", CaptionRevision = 7 };
        playback.Observe(first, At(1000));
        playback.Observe(first with { State = PlayState.Stopped }, At(1020));
        var blank = playback.Observe(Track(PlayState.Changing, ""), At(1050));
        Require(blank is { Title: "First", Artist: "Singer A", CaptionRevision: 7, IsTransition: true }, "an empty title retains caption and marquee revision");
        var second = Track(PlayState.Changing, "Second") with { Artist = "Singer B", CaptionRevision = 8 };
        var updated = playback.Observe(second, At(1080));
        Require(updated is { Title: "Second", Artist: "Singer B", CaptionRevision: 8, IsTransition: true } && updated.ObservedAt == At(1020),
            "valid metadata is applied during the hold without extending its deadline");
        var stillBlank = playback.Observe(Track(PlayState.Playing, ""), At(1090));
        Require(stillBlank is { Title: "Second", IsTransition: true } && stillBlank.ObservedAt == At(1020), "Playing with no title stays in the same transition");
        Require(playback.Observe(second with { State = PlayState.Playing }, At(1100)).IsPlaying, "Playing with the new title completes the transition early");
        var direct = playback.Observe(Track(PlayState.Playing, "Third"), At(1110));
        Require(direct is { Title: "Third", IsTransition: false }, "an ordinary title update applies immediately");
        var emptyWhilePlaying = playback.Observe(Track(PlayState.Playing, ""), At(1120));
        Require(emptyWhilePlaying.IsTransition && emptyWhilePlaying.Title == "Third", "an empty title starts a hold while Playing");
        Require(playback.Expire(At(1320)) is null, "the first switch timer cannot expire the newer switch");
        playback.Observe(Track(PlayState.Playing, "Fourth"), At(1330));
        Require(playback.Expire(At(1420)) is null && playback.Current.Title == "Fourth", "an expired callback cannot hide recovered playback");
    }

    private static void CheckDeadlines()
    {
        foreach (var fps in new[] { 30, 60 })
        {
            var playback = new PlaybackTransition();
            playback.Observe(Track(PlayState.Playing, "First"), At(1000));
            var stopped = Track(PlayState.Stopped, "First");
            playback.Observe(stopped, At(1100));
            for (var offset = 1000d / fps; offset < 299; offset += 1000d / fps)
            {
                Require(playback.Expire(At(1100 + offset)) is null, $"{fps} FPS: refresh before the deadline retains the title");
                var duplicate = playback.Observe(stopped, At(1100 + offset));
                Require(duplicate.IsTransition && duplicate.ObservedAt == At(1100), $"{fps} FPS: duplicate Stopped keeps the first timestamp");
            }

            Require(playback.Expire(At(1399)) is null && playback.Current.IsTransition, $"{fps} FPS: deadline has not passed at 299 ms");
            Require(playback.Expire(At(1400)) is { IsTransition: false, Title: "", AudioEligible: false }, $"{fps} FPS: clear on the 300 ms deadline");
            Require(!playback.Observe(stopped, At(1401)).IsTransition, $"{fps} FPS: repeat at 301 ms cannot restart the hold");
            Require(OverlayPlayback.Resolve(playback.Current, false, false, true) == new OverlayPlayback(false, true), "settings show the usual idle preview after a true stop");
            Require(playback.Observe(Track(PlayState.Playing, "Second"), At(1410)).IsPlaying, "late valid playback starts immediately after expiry");
        }
    }

    private static void CheckRemovalAndHandoff()
    {
        var playback = new PlaybackTransition();
        playback.Observe(Track(PlayState.Playing, "First"), At(1000));
        var stopped = new MediaCandidate(1, "kugou", PlayState.Stopped);
        var untitled = new MediaCandidate(2, "Other App", PlayState.Playing, HasTitle: false);
        var titled = untitled with { HasTitle = true };
        Require(playback.CanHold(stopped), "same stopped session is a candidate for the grace period");
        Require(MediaSelection.Pick([stopped, untitled], "kugou", 1, 1, transitioning: 1) == stopped, "an untitled alternate cannot displace a real caption in transition");
        Require(MediaSelection.Pick([stopped, titled], "kugou", 1, 1, transitioning: 1) == titled, "an eligible playing alternate takes over immediately");
        Require(MediaSelection.Pick([stopped with { State = PlayState.Closed }], "kugou", 1, 1, transitioning: 1) is null, "explicitly closed sessions are excluded");
        Require(!MediaSelection.CanIdentifyAudio(stopped with { State = PlayState.Playing }, [stopped, new(2, "KuGou", PlayState.Changing)]),
            "a different changing session with the same AUMID still makes recorder ownership ambiguous");

        playback.Observe(Track(PlayState.Stopped, "First"), At(1100));
        var replacement = playback.Observe(Track(PlayState.Stopped, "Replacement") with { SessionId = 2 }, At(1140));
        Require(!replacement.IsTransition && replacement is { Title: "", AudioEligible: false }, "a new same-source session cannot inherit the previous hold");
        var otherSource = Track(PlayState.Playing, "Other") with { Source = "Other App", SessionId = 3 };
        Require(playback.Observe(otherSource, At(1160)).IsPlaying, "the replacement is visible immediately when playing with metadata");
        playback.Observe(otherSource with { State = PlayState.Changing }, At(1200));
        Require(playback.Observe(TrackState.Empty, At(1210)) is { SessionId: 0, IsTransition: false, Title: "" }, "removal clears title immediately during a hold");
        Require(playback.Expire(At(1500)) is null, "the transition trigger is harmless after removal");
    }

    private static void CheckQueuedTimeouts()
    {
        var playback = new PlaybackTransition();
        playback.Observe(Track(PlayState.Playing, "First"), At(1000));
        playback.Observe(Track(PlayState.Stopped, "First"), At(1100));
        var firstTimer = playback.Generation;
        playback.Observe(Track(PlayState.Playing, "Second"), At(1200));
        playback.Observe(Track(PlayState.Changing, "Second"), At(1300));
        var secondTimer = playback.Generation;

        Require(firstTimer != secondTimer, "each hold schedules its own timeout");
        Require(playback.Expire(At(1620), firstTimer) is null, "a queued timer from the first hold cannot expire the second after a worker stall");
        Require(playback.Expire(At(1599), secondTimer) is null, "an early timer notification stays early when processed after a worker stall");
        Require(playback.Observe(Track(PlayState.Playing, "Third"), At(1599.5)).IsPlaying, "timely Playing in the backlog can finish the hold");
        Require(playback.Expire(At(1620), secondTimer) is null, "a queued timer cannot clear the now-current Playing state");
    }

    private static void CheckPauseAndUserControls()
    {
        var playback = new PlaybackTransition();
        var settings = AppSettings.Defaults with { LiveCaptureEnabled = true, SpectrumEnabled = true };
        playback.Observe(Track(PlayState.Playing, "First"), At(1000));
        var transition = playback.Observe(Track(PlayState.Stopped, ""), At(1100));
        Require(OverlayPlayback.Resolve(transition, false, true, true) == new OverlayPlayback(false, false), "manually hiding during a hold suppresses both windows");
        Require(!SpectrumPresentation.WantsCapture(settings, transition, true, false), "a hold cannot open a recorder");
        var live = new AudioCaptureStatus(AudioCaptureState.Live, AudioCaptureReason.None, true, "kugou", 4);
        var disabled = SpectrumPresentation.Resolve(settings with { SpectrumEnabled = false }, transition, false, false, true, false, live, 4);
        var noCapture = SpectrumPresentation.Resolve(settings with { LiveCaptureEnabled = false }, transition, false, false, true, false, live, 4);
        var keeping = SpectrumPresentation.Resolve(settings, transition, false, false, true, false, live, 4);
        Require(disabled.Mode == SpectrumMode.Disabled && noCapture.Mode == SpectrumMode.Transitioning && keeping.Mode == SpectrumMode.Live,
            "settings apply immediately, while a matching live connection drives its own decay");
        Require(!noCapture.UsesPreset && noCapture.DrawsFrames(true, true) && !noCapture.DrawsFrames(true, false), "a disconnected transition fades existing bars to rest without a preset");

        var paused = playback.Observe(Track(PlayState.Paused, ""), At(1180));
        Require(paused is { Title: "First", IsTransition: false, AudioEligible: false } && paused.ObservedAt == At(1180), "explicit pause ends the transition and records its own starting time");
        var newMetadata = playback.Observe(Track(PlayState.Paused, "Renamed"), At(2700));
        Require(newMetadata.ObservedAt == At(1180), "paused metadata cannot restart the pause tail");
        Require(OverlayPlayback.PauseRemaining(newMetadata.ObservedAt, At(2700)) == TimeSpan.FromMilliseconds(480), "a UI backlog does not extend the pause countdown");
        Require(OverlayPlayback.PauseRemaining(newMetadata.ObservedAt, At(3180)) == TimeSpan.Zero, "pause tail expires two seconds after the pause event");
        Require(OverlayPlayback.Resolve(paused, true, false, true).ShowReal, "the pause tail keeps the real title visible");
        Require(OverlayPlayback.Resolve(paused, false, false, true).ShowPreview, "idle preview follows the pause tail");
        var resumed = playback.Observe(Track(PlayState.Playing, "Renamed"), At(3000));
        Require(OverlayPlayback.Resolve(resumed, false, false, true).ShowReal, "resuming restores real playback before an old pause trigger can hide it");
        Require(OverlayPlayback.Resolve(resumed, false, true, true) == new OverlayPlayback(false, false), "later Playing honors the manual hide preference");
    }

    private static TrackState Track(PlayState state, string title) => new("kugou", title, "", state) { SessionId = 1 };

    private static long At(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
