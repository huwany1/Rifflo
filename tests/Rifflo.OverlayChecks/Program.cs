using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rifflo.Audio;
using Rifflo.Media;
using Rifflo.Overlay;
using Rifflo.Settings;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--rendering-smoke", "default" or "software"])
        {
            RenderingSmoke.Run(args[1] == "software");
            return;
        }
        Require(args.Length == 0, "Usage: Rifflo.OverlayChecks [--rendering-smoke default|software]");

        CheckFrameCadence();
        CheckIdleClock();
        CheckMarquee();
        CheckText();
        CheckViewportBoundary();
        CheckMarqueePixels();
        CheckTrackRevision();
        CheckLayout();
        CheckDefaultMarginOnNarrowScreen();
        CheckSpectrumDrawing();
        CheckSpectrumModes();
        CheckPresetAnimation();
        CheckSpectrumSettings();
        PlaybackTransitionChecks.Run();
        BroadcastCaptureWindowChecks.Run();
        Console.WriteLine("OverlayChecks passed: 144 Hz cadence, stalled frames, switch continuity, idle scheduling, marquee timing and subpixel rendering, text, layout, spectrum modes, preset rendering and fade, and settings migration.");
    }

    private static void CheckFrameCadence()
    {
        const int refreshRate = 144;
        long start = Stopwatch.Frequency;
        foreach (var target in new[] { 30, 60 })
        {
            var cadence = new FrameCadence(target);
            var count = 0;
            var previous = start;
            for (var i = 0; i < refreshRate * 10; i++)
            {
                long now = start + i * Stopwatch.Frequency / refreshRate;
                if (!cadence.TryAdvance(now))
                    continue;

                Require(!cadence.TryAdvance(now), "duplicate rendering timestamp cannot submit another frame");
                if (count > 0)
                    Require(Stopwatch.GetElapsedTime(previous, now).TotalSeconds <= 1.0 / target + 1.0 / refreshRate,
                        $"{target} FPS at 144 Hz keeps frame intervals within one display refresh of target");
                previous = now;
                count++;
            }
            Require(Math.Abs(count - target * 10) <= 1, $"{target} FPS at 144 Hz emits {count} updates over ten seconds");

            var stalledAt = start + Stopwatch.Frequency * 12;
            Require(cadence.TryAdvance(stalledAt), "first callback after a UI stall emits");
            Require(!cadence.TryAdvance(stalledAt) && !cadence.TryAdvance(stalledAt + Stopwatch.Frequency / 1_000),
                "a UI stall does not replay overdue frames");
        }

        var switched = new FrameCadence(30);
        var lastTime = 0.0;
        long firstSwitchAt = start + 3 * Stopwatch.Frequency;
        long secondSwitchAt = start + 4 * Stopwatch.Frequency;
        for (var i = 0; i < refreshRate * 5; i++)
        {
            long now = start + i * Stopwatch.Frequency / refreshRate;
            if (now == firstSwitchAt)
                switched.SetFrameRate(60);
            else if (now == secondSwitchAt)
                switched.SetFrameRate(30);
            if (!switched.TryAdvance(now))
                continue;

            var elapsed = Stopwatch.GetElapsedTime(start, now).TotalSeconds;
            if (lastTime >= CaptionMarquee.HoldSeconds)
            {
                foreach (var mode in Enum.GetValues<CaptionScrollMode>())
                {
                    var before = CaptionMarquee.GetOffset(180, mode, lastTime);
                    var after = CaptionMarquee.GetOffset(180, mode, elapsed);
                    Check(30 * (elapsed - lastTime), after - before, $"{mode} moves at 30 DIP/s through both cadence changes");
                }
            }
            lastTime = elapsed;
        }
    }

    private static void CheckIdleClock()
    {
        using var clock = new OverlayFrameClock();
        var caption = new CaptionPanel(clock);
        caption.SetTrack("短", "");
        Arrange(caption);
        Require(!caption.IsScrolling && !clock.IsRunning, "fitting caption starts no wake or shared rendering callback");

        caption.Visibility = Visibility.Collapsed;
        caption.SetTrack(new string('W', 80), "");
        Arrange(caption);
        Require(!caption.IsScrolling && !clock.IsRunning, "hidden caption starts no wake or shared rendering callback");

        Action<long> captionTick = _ => { };
        Action<long> spectrumTick = _ => { };
        for (var i = 0; i < 3; i++)
        {
            clock.Tick += captionTick;
            clock.Tick += spectrumTick;
            clock.SetFrameRate(60);
            clock.Tick -= spectrumTick;
            Require(clock.IsRunning, "a caption can run independently after spectrum unsubscribes");
            clock.Tick -= captionTick;
            clock.SetFrameRate(30);
            Require(!clock.IsRunning, "no shared rendering callback remains after the final unsubscribe and frame rate change");
        }

        clock.Tick += spectrumTick;
        clock.Dispose();
        Require(!clock.IsRunning, "disposing an active animation clock releases all listeners");
    }

    private static void CheckMarquee()
    {
        const double distance = 90;
        foreach (var mode in Enum.GetValues<CaptionScrollMode>())
        {
            Check(0, CaptionMarquee.GetOffset(0, mode, 10), $"{mode} fits exactly");
            Check(0, CaptionMarquee.GetOffset(-1, mode, 10), $"{mode} short text");
            Check(0, CaptionMarquee.GetOffset(distance, mode, 1.99), $"{mode} start hold");
            Check(45, CaptionMarquee.GetOffset(distance, mode, 3.5), $"{mode} outward midpoint");
            Check(90, CaptionMarquee.GetOffset(distance, mode, 5), $"{mode} far end");
            Check(90, CaptionMarquee.GetOffset(distance, mode, 6.99), $"{mode} end hold");
            Check(0.3, CaptionMarquee.GetOffset(1, mode, 2.01), $"{mode} subpixel overflow");
        }

        Check(0, CaptionMarquee.GetOffset(distance, CaptionScrollMode.Loop, 7), "loop jumps to the start");
        Check(0, CaptionMarquee.GetOffset(distance, CaptionScrollMode.Loop, 8.99), "loop next start hold");
        Check(30, CaptionMarquee.GetOffset(distance, CaptionScrollMode.Loop, 10), "loop moves left again");
        Check(90, CaptionMarquee.GetOffset(distance, CaptionScrollMode.Bounce, 7), "bounce starts at far end");
        Check(45, CaptionMarquee.GetOffset(distance, CaptionScrollMode.Bounce, 8.5), "bounce return midpoint");
        Check(0, CaptionMarquee.GetOffset(distance, CaptionScrollMode.Bounce, 10), "bounce reaches the start");
        Check(0, CaptionMarquee.GetOffset(distance, CaptionScrollMode.Bounce, 11.99), "bounce next start hold");
        Check(30, CaptionMarquee.GetOffset(distance, CaptionScrollMode.Bounce, 13), "bounce moves left again");
    }

    private static void CheckText()
    {
        const string example = "That Girl&Talking to the Moon (Full)";
        const string artist = "4AGX";
        var caption = new CaptionPanel();
        caption.SetTrack(example, artist);
        Arrange(caption);
        Check(260, caption.ActualWidth, "default viewport width");
        Require(caption.ContentWidth > caption.ActualWidth, "example overflows the default width");

        var exactWidth = caption.ContentWidth;
        caption.SetViewport(exactWidth, CaptionScrollMode.Bounce);
        Arrange(caption);
        Check(caption.ActualWidth, caption.ContentWidth, "matching viewport and text width");
        caption.SetViewport(exactWidth - 1, CaptionScrollMode.Bounce);
        Arrange(caption);
        Check(1, caption.ContentWidth - caption.ActualWidth, "one DIP overflow");
        caption.SetTrack("歌", "");
        Arrange(caption);
        Check(exactWidth - 1, caption.ActualWidth, "short track retains width");
        Require(caption.ContentWidth < caption.ActualWidth, "short track fits");

        foreach (var pixelsPerDip in new[] { 1.0, 1.25, 1.5 })
        {
            var titleOnly = CaptionDrawing.Create(example, "", 20, Brushes.White, pixelsPerDip);
            var withArtist = CaptionDrawing.Create(example, artist, 20, Brushes.White, pixelsPerDip);
            Require(withArtist.Width - titleOnly.Width > 12, "artist and separator extend the text");
            VerifyEndGlyph(withArtist, 160, pixelsPerDip);

            var mixed = CaptionDrawing.Create("全角（未完） / Music 🎸", "歌手·TestX", 20, Brushes.White, pixelsPerDip);
            Require(mixed.Width > 0 && mixed.Height > 0, "mixed Unicode measures as one line");
            VerifyEndGlyph(mixed, 160, pixelsPerDip);
        }

        var longTitle = new string('W', 8190) + "🎸中文" + new string('W', 1800);
        var longArtist = new string('M', 10000) + "X";
        foreach (var formattingMode in Enum.GetValues<TextFormattingMode>())
        {
            var veryLong = CaptionDrawing.Create(longTitle, longArtist, 20, Brushes.White, 1, formattingMode);
            var shorter = CaptionDrawing.Create(longTitle[..8190], "", 20, Brushes.White, 1, formattingMode);
            Require(veryLong.Width > shorter.Width * 2, $"{formattingMode} renders both fields beyond WPF's line limit");
            Check(shorter.Height, veryLong.Height, $"{formattingMode} keeps long fields on a single line", tolerance: 5);
            VerifyEndGlyph(veryLong, 160, 1);
        }
    }

    private static void VerifyEndGlyph(CaptionDrawing line, double viewportWidth, double pixelsPerDip)
    {
        var distance = Math.Max(0, line.Width - viewportWidth);
        var dpi = 96 * pixelsPerDip;
        var height = (int)Math.Ceiling(line.Height * pixelsPerDip);
        var width = (int)Math.Ceiling(viewportWidth * pixelsPerDip);
        var image = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushClip(new RectangleGeometry(new Rect(0, 0, viewportWidth, line.Height)));
            context.PushTransform(new TranslateTransform(-distance, 0));
            context.DrawDrawing(line.Drawing);
            context.Pop();
            context.Pop();
        }
        image.Render(visual);

        var right = (int)Math.Ceiling((line.Drawing.Bounds.Right - distance) * pixelsPerDip);
        Require(right <= width && right > 4, "ending ink fits inside the viewport");
        var alpha = new byte[4 * 5 * height];
        image.CopyPixels(new Int32Rect(right - 5, 0, 5, height), alpha, 4 * 5, 0);
        Require(alpha.Where((_, index) => index % 4 == 3).Any(value => value > 100),
            "terminal glyph draws at the measured end");
    }

    private static void CheckMarqueePixels()
    {
        const double scale = 1.25;
        var caption = new CaptionPanel { UseLayoutRounding = true };
        VisualTreeHelper.SetRootDpi(caption, new DpiScale(scale, scale));
        caption.SetAppearance(20, Brushes.White);
        caption.SetTrack("That Girl&Talking to the Moon (Full)", "4AGX");
        caption.SetViewport(160, CaptionScrollMode.Loop);
        Arrange(caption);
        Require(caption.ContentWidth > caption.ActualWidth + 6, "fractional motion test uses an overflowing title");

        // Exercise the retained caption transform without waiting for the real-time marquee clock.
        var setOffset = typeof(CaptionPanel).GetMethod("SetOffset", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<double>>(caption);
        var width = (int)Math.Ceiling(caption.ActualWidth * scale);
        var height = (int)Math.Ceiling(caption.ActualHeight * scale);
        var current = new byte[width * height * 4];
        var previous = new byte[current.Length];
        for (var step = 0; step < 12; step++)
        {
            setOffset(step * 0.5); // 30 DIP/s at 60 FPS is less than one physical pixel at 125% DPI.
            var image = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(caption);
            image.CopyPixels(current, width * 4, 0);
            Require(step == 0 || !current.AsSpan().SequenceEqual(previous), $"caption pixels change on subpixel step {step}");
            (current, previous) = (previous, current);
        }
    }

    private static void CheckViewportBoundary()
    {
        const double scale = 1.25;
        foreach (var (title, artist) in new[]
        {
            ("That Girl&Talking to the Moon (Full)", "4AGX"),
            ("中英混排：Testing 🎶／全角・VeryLongWordWithoutAnySpace", "艺人 Artists 🎸"),
            (new string('W', 31) + "X", "")
        })
        {
            var caption = new CaptionPanel { UseLayoutRounding = true };
            VisualTreeHelper.SetRootDpi(caption, new DpiScale(scale, scale));
            caption.SetTrack(title, artist);
            caption.SetViewport(1200, CaptionScrollMode.Bounce);
            Arrange(caption);
            var width = caption.ContentWidth;
            var firstFittingWidth = Math.Ceiling(width * scale) / scale;

            caption.SetViewport(firstFittingWidth, CaptionScrollMode.Bounce);
            Arrange(caption);
            Require(caption.ContentWidth <= caption.ActualWidth + 1e-6, $"'{title}' fits at its first full pixel width");
            Check(width, caption.ContentWidth, $"'{title}' keeps its measured width when fitting exactly");

            caption.SetViewport(firstFittingWidth - 2 / scale, CaptionScrollMode.Bounce);
            Arrange(caption);
            Require(caption.ContentWidth > caption.ActualWidth, $"'{title}' overflows when two device pixels narrower");
            Check(width, caption.ContentWidth, $"'{title}' keeps the same endpoint after switching glyph rendering");

            var display = CaptionDrawing.Create(title, artist, 20, Brushes.White, scale);
            var ideal = CaptionDrawing.Create(title, artist, 20, Brushes.White, scale, TextFormattingMode.Ideal);
            var marquee = ideal.MatchLayout(display);
            Check(width, marquee.Width, $"'{title}' uses Display metrics when moving");
            VerifyEndGlyph(marquee, 160, scale);
        }
    }

    private static void CheckTrackRevision()
    {
        const string title = "That Girl&Talking to the Moon (Full)";
        const string artist = "4AGX";
        var caption = new CaptionPanel();
        caption.SetTrack(title, artist, "player", revision: 1);
        Arrange(caption);
        var transform = FindTranslation(VisualTreeHelper.GetDrawing(caption))
            ?? throw new InvalidOperationException("Caption did not retain a text translation.");
        var width = caption.ContentWidth;

        transform.X = -30;
        Check(30, caption.Offset, "simulated time sample is past the start hold");
        caption.SetTrack(title, artist, "PLAYER", revision: 1);
        Check(30, caption.Offset, "repeated metadata and revision keep the current offset");
        caption.SetTrack(title, artist, "player", revision: 2);
        Check(0, caption.Offset, "different revision restarts an identically named song");
        Check(width, caption.ContentWidth, "revision alone preserves the measured caption");
    }

    private static TranslateTransform? FindTranslation(DrawingGroup? drawing)
    {
        if (drawing is null)
            return null;
        if (drawing.Transform is TranslateTransform translation)
            return translation;

        return drawing.Children.OfType<DrawingGroup>().Select(FindTranslation).FirstOrDefault(transform => transform is not null);
    }

    private static void CheckLayout()
    {
        foreach (var mode in Enum.GetValues<CaptionScrollMode>())
        foreach (var direction in Enum.GetValues<OverlayLayout>())
        {
            var caption = new CaptionPanel();
            caption.SetViewport(260, mode);
            caption.SetTrack("Very very very very very long song", "Another long artist");
            var spectrum = new SpectrumView();
            var layout = new OverlayLayoutPanel(caption, spectrum);
            layout.SetLayout(direction);
            Arrange(layout);
            var c = VisualTreeHelper.GetOffset(caption);
            var s = VisualTreeHelper.GetOffset(spectrum);
            if (direction is OverlayLayout.SpectrumAbove or OverlayLayout.CaptionAbove)
            {
                Check(c.X, s.X, $"{direction} left alignment");
                if (direction == OverlayLayout.SpectrumAbove)
                    Check(s.Y + spectrum.ActualHeight + 5, c.Y, "spectrum above caption");
                else
                    Check(c.Y + caption.ActualHeight + 5, s.Y, "caption above spectrum");
            }
            else
            {
                Check(c.Y + caption.ActualHeight, s.Y + spectrum.ActualHeight, $"{direction} bottom alignment");
                if (direction == OverlayLayout.CaptionLeft)
                    Check(c.X + caption.ActualWidth + 12, s.X, "caption left of spectrum");
                else
                    Check(s.X + spectrum.ActualWidth + 12, c.X, "spectrum left of caption");
            }

            var spectrumWidth = spectrum.ActualWidth;
            var spectrumHeight = spectrum.ActualHeight;
            caption.SetTrack("Short", "");
            Arrange(layout);
            Check(260, caption.ActualWidth, "text viewport width is stable when the track changes");
            Check(spectrumWidth, spectrum.ActualWidth, "spectrum width is stable when the track changes");
            Check(spectrumHeight, spectrum.ActualHeight, "spectrum height is stable when the track changes");

            spectrum.Visibility = Visibility.Collapsed;
            Arrange(layout);
            Check(caption.ActualWidth, layout.ActualWidth, "caption only removes horizontal spectrum gap");
            Check(caption.ActualHeight, layout.ActualHeight, "caption only removes vertical spectrum gap");
            spectrum.Visibility = Visibility.Visible;

            layout.MaxWidth = 200;
            Arrange(layout);
            Require(caption.ActualWidth < 260 && caption.ActualWidth > 0, "narrow work area shrinks viewport");
            Require(layout.ActualWidth <= 200, "narrow work area contains both controls");
            layout.MaxWidth = double.PositiveInfinity;
            Arrange(layout);
            Check(260, caption.ActualWidth, "wide work area restores configured width");
        }
    }

    private static void CheckSpectrumDrawing()
    {
        var spectrum = new SpectrumView();
        var levels = Enumerable.Repeat(0.4f, 16).ToArray();
        levels[7] = 1;
        spectrum.SetBands(levels);
        Arrange(spectrum);
        var drawing = VisualTreeHelper.GetDrawing(spectrum)?.Bounds ?? Rect.Empty;
        Check(0, drawing.Left, "outer stroke starts on the spectrum's left edge");
        Check(0, drawing.Top, "full bar reaches the top of the spectrum");
        Check(spectrum.ActualHeight, drawing.Bottom, "outer stroke ends at the bar baseline");
        Check(spectrum.ActualWidth, drawing.Right, "last bar stays inside the spectrum");
    }

    private static void CheckPresetAnimation()
    {
        foreach (var count in new[] { 8, 19, 32 })
        {
            var first = new float[count];
            var second = new float[count];
            var wrapped = new float[count];
            SpectrumAnimation.FillPreset(first, 0, 1.0);
            SpectrumAnimation.FillPreset(second, 0.24, 1.0);
            SpectrumAnimation.FillPreset(wrapped, SpectrumAnimation.PresetPeriodSeconds, 1.0);
            Require(first.SequenceEqual(wrapped), $"{count} bars repeat exactly at the loop point");
            SpectrumAnimation.FillPreset(wrapped, SpectrumAnimation.PresetPeriodSeconds - 0.001, 1.0);
            Require(first.Zip(wrapped).All(pair => Math.Abs(pair.First - pair.Second) < 0.01), $"{count} bars cross the seam smoothly");
            Require(first.Zip(second).Count(pair => Math.Abs(pair.First - pair.Second) > 0.025) >= count / 2,
                $"{count} bars vary over time");
            Require(first.Zip(second).Any(pair => pair.Second > pair.First) && first.Zip(second).Any(pair => pair.Second < pair.First),
                $"{count} bars move in different directions");

            foreach (var direction in Enum.GetValues<OverlayLayout>())
            {
                var caption = new CaptionPanel();
                caption.SetTrack("Long track title for preset layout measurement", "Artist");
                var spectrum = new SpectrumView();
                spectrum.SetAppearance(count, Brushes.LightBlue);
                var layout = new OverlayLayoutPanel(caption, spectrum);
                layout.SetLayout(direction);
                spectrum.SetBands(first);
                Arrange(layout);
                var size = layout.RenderSize;
                var captionOrigin = VisualTreeHelper.GetOffset(caption);
                var spectrumOrigin = VisualTreeHelper.GetOffset(spectrum);
                var firstHeights = spectrum.RenderedBarHeights.ToArray();
                var drawCount = spectrum.DrawCount;
                Require(firstHeights.Length == count && firstHeights.All(height => height > 2), $"{count} bars render in {direction}");

                spectrum.SetBands(second);
                // This disconnected WPF visual has no dispatcher-driven window layout pass.
                spectrum.Arrange(new Rect(new Point(spectrumOrigin.X, spectrumOrigin.Y), spectrum.RenderSize));
                Require(spectrum.DrawCount > drawCount && !spectrum.RenderedBarHeights.SequenceEqual(firstHeights),
                    $"{direction} draws changing preset heights");
                Require(layout.RenderSize == size && VisualTreeHelper.GetOffset(caption) == captionOrigin &&
                    VisualTreeHelper.GetOffset(spectrum) == spectrumOrigin, $"{direction} geometry stays fixed across frames");
            }

            var beforePause = (float[])second.Clone();
            Require(SpectrumAnimation.FadeToSilence(second, 0) && second.SequenceEqual(beforePause), "pausing starts from the current frame");
            Require(SpectrumAnimation.FadeToSilence(second, 0.25) && second.Zip(beforePause).All(pair => pair.First > 0 && pair.First < pair.Second),
                "pause lowers the existing bars");
            Require(!SpectrumAnimation.FadeToSilence(second, 1.75) && second.All(level => level == 0), "pause animation stops within the tail");
        }
    }

    private static void CheckSpectrumModes()
    {
        var settings = AppSettings.Defaults;
        var manual = settings with { LiveCaptureEnabled = false };
        var disabled = manual with { SpectrumEnabled = false };
        var track = new TrackState("fixture-1", "Test song", "", PlayState.Playing);
        var live = new AudioCaptureStatus(AudioCaptureState.Live, AudioCaptureReason.None, true, track.Source, 1);
        var connecting = live with { State = AudioCaptureState.Connecting, Recording = false };
        var fallback = connecting with { State = AudioCaptureState.Fallback, Reason = AudioCaptureReason.NoPackets };
        var muted = connecting with { State = AudioCaptureState.Muted };

        CheckMode("fresh connection", settings, track, connecting, SpectrumMode.Connecting, captureWanted: true, draws: false);
        CheckMode("healthy realtime", settings, track, live, SpectrumMode.Live, captureWanted: true, draws: true);
        CheckMode("manual override despite previous live capture", manual, track, live, SpectrumMode.ManualPreset, captureWanted: false, draws: true);
        CheckMode("display off supersedes saved manual preference", disabled, track, live, SpectrumMode.Disabled, captureWanted: false, draws: false);
        CheckMode("hidden supersedes saved realtime preference", settings, track, live, SpectrumMode.Hidden, captureWanted: false, draws: false,
            manuallyHidden: true);
        CheckMode("capture failure draws and retains retry", settings, track, fallback, SpectrumMode.AutomaticPreset, captureWanted: true, draws: true,
            reason: AudioCaptureReason.NoPackets);
        CheckMode("ambiguous media identity stops old capture", settings, track with { AudioEligible = false }, live,
            SpectrumMode.AutomaticPreset, captureWanted: false, draws: true, reason: AudioCaptureReason.AmbiguousSource);
        CheckMode("late result for the previous player is ignored", settings, track with { Source = "fixture-2" }, live,
            SpectrumMode.Connecting, captureWanted: true, draws: false);
        CheckMode("healthy connection on a same-player track change", settings, track with { Title = "New song" }, live,
            SpectrumMode.Live, captureWanted: true, draws: true);
        var competingPlayer = track with { AudioCompetition = AudioCompetition.FromSources(["another-aumid"]) };
        var mixed = fallback with { Reason = AudioCaptureReason.AmbiguousSource, Generation = 2 };
        CheckMode("old live capture cannot draw after competing AUMID appears", settings, competingPlayer, live,
            SpectrumMode.Connecting, captureWanted: true, draws: false, requestedGeneration: 2);
        CheckMode("shared browser audio tree draws automatic preset", settings, competingPlayer, mixed,
            SpectrumMode.AutomaticPreset, captureWanted: true, draws: true, reason: AudioCaptureReason.AmbiguousSource);
        CheckMode("stale ambiguity cannot draw once the competitor leaves", settings, track, mixed,
            SpectrumMode.Connecting, captureWanted: true, draws: false, requestedGeneration: 3);
        CheckMode("healthy browser audio draws after the competitor leaves", settings, track, live with { Generation = 3 },
            SpectrumMode.Live, captureWanted: true, draws: true);
        CheckMode("manual preset still takes precedence during shared-source recovery", manual, track, live with { Generation = 3 },
            SpectrumMode.ManualPreset, captureWanted: false, draws: true);
        CheckMode("explicit mute fades existing bars", settings, track, muted, SpectrumMode.Muted, captureWanted: true, draws: true);
        CheckMode("explicit mute stops drawing once settled", settings, track, muted, SpectrumMode.Muted, captureWanted: true, draws: false,
            hasLevels: false);

        var paused = track with { State = PlayState.Paused };
        CheckMode("pause fades real or preset bars", manual, paused, live, SpectrumMode.Pausing, captureWanted: false, draws: true,
            showPauseSpectrum: true);
        CheckMode("pause stops drawing once the bars settle", manual, paused, live, SpectrumMode.Pausing, captureWanted: false, draws: false,
            showPauseSpectrum: true, hasLevels: false);
        CheckMode("pause tail ends", settings, paused, live, SpectrumMode.None, captureWanted: false, draws: false);
        CheckMode("stopped or no title has no playback overlay", settings, TrackState.Empty, fallback, SpectrumMode.None, captureWanted: false, draws: false);
        CheckMode("no playback settings preview has sample bars", settings, TrackState.Empty, fallback, SpectrumMode.Preview, captureWanted: false, draws: true,
            showPreview: true);
        CheckMode("disabled display leaves a caption-only preview", disabled, TrackState.Empty, fallback, SpectrumMode.Preview, captureWanted: false, draws: false,
            showPreview: true);

        static void CheckMode(string name, AppSettings settings, TrackState track, AudioCaptureStatus capture, SpectrumMode mode,
            bool captureWanted, bool draws, bool manuallyHidden = false, bool showPauseSpectrum = false, bool showPreview = false,
            bool hasLevels = true, AudioCaptureReason reason = AudioCaptureReason.None, long? requestedGeneration = null)
        {
            var playing = track.State == PlayState.Playing && track.Title.Length > 0;
            var presentation = SpectrumPresentation.Resolve(settings, track, playing, manuallyHidden, showPauseSpectrum, showPreview, capture,
                requestedGeneration ?? capture.Generation);
            Require(presentation == new SpectrumPresentation(mode, reason), $"{name}: presentation mode and reason");
            Require(presentation.DrawsFrames(settings.SpectrumEnabled, hasLevels) == draws, $"{name}: frame timer state");
            Require(SpectrumPresentation.WantsCapture(settings, track, playing, manuallyHidden) == captureWanted, $"{name}: capture request state");
        }
    }

    private static void CheckSpectrumSettings()
    {
        Require(AppSettings.Defaults.SpectrumEnabled && AppSettings.Defaults.LiveCaptureEnabled, "restoring defaults enables both switches");
        var path = Path.GetTempFileName();
        try
        {
            var store = new SettingsStore(path);
            File.WriteAllText(path, """{"spectrumEnabled":false,"barCount":25,"position":{"x":184,"y":672}}""");
            var old = store.Load();
            Require(!old.SpectrumEnabled && old.LiveCaptureEnabled, "migration preserves disabled display and defaults capture on");
            Require(old.BarCount == 25 && old.Position is { X: 184, Y: 672 }, "migration preserves prior appearance and position");
            File.WriteAllText(path, """{"spectrumEnabled":false,"liveCaptureEnabled":"bad","barCount":25}""");
            var malformed = store.Load();
            Require(!malformed.SpectrumEnabled && malformed.LiveCaptureEnabled && malformed.BarCount == 25,
                "malformed capture preference uses its default and keeps the remaining settings");

            foreach (var show in new[] { false, true })
            foreach (var capture in new[] { false, true })
            {
                var switches = old with { SpectrumEnabled = show, LiveCaptureEnabled = capture };
                Require(store.Save(switches) && new SettingsStore(path).Load() == switches,
                    $"both preferences persist independently (display={show}, capture={capture})");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CheckDefaultMarginOnNarrowScreen()
    {
        const double insets = 26;
        foreach (var dpi in new uint[] { 96, 120, 144, 113 })
        {
            var work = new WorkArea(-400, 50, -400 + PlacementMath.ToPixels(250, dpi), 850, dpi);
            var caption = new CaptionPanel();
            caption.SetViewport(640, CaptionScrollMode.Loop);
            caption.SetTrack("A long title for a 250 DIP work area", "Artist name");
            var spectrum = new SpectrumView();
            spectrum.SetAppearance(32, Brushes.White);
            var layout = new OverlayLayoutPanel(caption, spectrum);
            layout.SetLayout(OverlayLayout.SpectrumLeft);
            var windowContent = new Border { Child = layout, Padding = new Thickness(12, 7, 12, 8), BorderThickness = new Thickness(1) };

            layout.MaxWidth = OverlayLayoutPanel.WorkAreaContentWidth(work, insets, reserveDefaultMargin: true);
            Arrange(windowContent);
            var margin = PlacementMath.ToPixels(24, dpi);
            var windowWidth = PlacementMath.ToPixels(windowContent.ActualWidth, dpi);
            var placement = PlacementMath.Resolve(null, null, work, windowContent.ActualWidth, windowContent.ActualHeight);
            var recovery = PlacementMath.Resolve(new OverlayPosition(1, 1), null, work, windowContent.ActualWidth, windowContent.ActualHeight);
            Check(work.Left + margin, placement.Position.X, $"{dpi} DPI narrow default left margin");
            Check(placement.Position.X, recovery.Position.X, $"{dpi} DPI narrow recovery left margin");
            Require(placement.Position.X + windowWidth <= work.Right, $"{dpi} DPI default window stays within the work area");
            Require(caption.ActualWidth > 0 && spectrum.ActualWidth > 0, $"{dpi} DPI narrow horizontal layout retains both controls");

            var availableWidth = layout.MaxWidth;
            layout.MaxWidth = OverlayLayoutPanel.WorkAreaContentWidth(work, insets, reserveDefaultMargin: false);
            Check(margin * 96.0 / dpi, layout.MaxWidth - availableWidth, $"{dpi} DPI saved-position layout can use the extra width");
        }

        var smallerThanInsets = new WorkArea(0, 0, 25, 100, 96);
        Check(0, OverlayLayoutPanel.WorkAreaContentWidth(smallerThanInsets, insets, reserveDefaultMargin: true), "tiny width is bounded by zero");
    }

    private static void Arrange(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(new Point(0, 0), element.DesiredSize));
    }

    private static void Check(double expected, double actual, string name, double tolerance = 1e-6) =>
        Require(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"{name}, expected {expected}, got {actual}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
