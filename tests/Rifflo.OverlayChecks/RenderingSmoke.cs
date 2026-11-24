using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rifflo.Overlay;
using Rifflo.Settings;

internal static class RenderingSmoke
{
    public static void Run(bool software)
    {
        RenderOptions.ProcessRenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;
        Console.WriteLine($"WPF process render mode {RenderOptions.ProcessRenderMode}, device rendering tier {RenderCapability.Tier >> 16}.");

        using var clock = new OverlayFrameClock();
        var overlay = new OverlayWindow(preview: false, clock, traceFrames: true);
        var settings = AppSettings.Defaults with { Layout = OverlayLayout.SpectrumLeft };
        overlay.ApplySettings(settings);
        overlay.SetTrack(string.Concat(Enumerable.Repeat("Long waves with a rolling caption / ", 12)), "Rifflo rendering check");
        overlay.SetSpectrumVisible(true);

        var levels = new float[settings.BarCount];
        var start = Stopwatch.GetTimestamp();
        void Draw(long now)
        {
            SpectrumAnimation.FillPreset(levels, Stopwatch.GetElapsedTime(start, now).TotalSeconds, settings.Sensitivity);
            overlay.SetBands(levels);
        }

        try
        {
            overlay.SetShown(true);
            var hwnd = new WindowInteropHelper(overlay).Handle;
            Require(overlay.IsVisible && hwnd != 0 && overlay.AllowsTransparency, "Layered overlay did not show.");
            clock.Tick += Draw;
            Pump(TimeSpan.FromSeconds(2.4));

            var low = Sample(overlay, clock, 30);
            var high = Sample(overlay, clock, 60);
            var back = Sample(overlay, clock, 30);
            Require(high.Caption / low.Caption >= 1.4 && high.Spectrum / low.Spectrum >= 1.4,
                "Both visual paths should submit substantially more changes after switching to 60 FPS.");
            Require(Math.Abs(back.Caption - low.Caption) <= 5 && Math.Abs(back.Spectrum - low.Spectrum) <= 5,
                "Switching back should restore the lower cadence.");
            Require(hwnd == new WindowInteropHelper(overlay).Handle, "The switch changed the native window handle.");

            var layout = overlay.GetLayoutDiagnostics();
            Require(layout.Caption is { Width: > 0 } && layout.Spectrum is { Width: > 0 } && layout.DpiScale > 0,
                "DPI/layout did not produce visible caption and spectrum geometry.");
            CheckPixels(overlay, layout);

            clock.Tick -= Draw;
            overlay.SetShown(false);
            var changes = overlay.SpectrumChangedCount;
            var offsets = overlay.CaptionOffsetCommitCount;
            Pump(TimeSpan.FromMilliseconds(160));
            Require(!clock.IsRunning && changes == overlay.SpectrumChangedCount && offsets == overlay.CaptionOffsetCommitCount,
                "Hiding both visual paths should stop subscriptions and geometry changes.");
            Console.WriteLine($"DPI scale {layout.DpiScale:0.00}. Stable native window identity, caption and spectrum pixels, and idle after hide.");
        }
        finally
        {
            clock.Tick -= Draw;
            overlay.Close();
        }
    }

    private static (double Spectrum, double Caption) Sample(OverlayWindow overlay, OverlayFrameClock clock, int fps)
    {
        var previousOffset = overlay.GetLayoutDiagnostics().CaptionOffset;
        clock.SetFrameRate(fps);
        var from = Stopwatch.GetTimestamp();
        Pump(TimeSpan.FromSeconds(1.5));
        var to = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(from, to).TotalSeconds;
        var spectrum = overlay.GetSpectrumFrames().Count(frame => frame.Timestamp >= from && frame.Timestamp <= to) / seconds;
        var caption = overlay.GetCaptionFrames().Count(frame => frame.Timestamp >= from && frame.Timestamp <= to) / seconds;
        var distance = overlay.GetLayoutDiagnostics().CaptionOffset - previousOffset;
        Console.WriteLine($"{fps} FPS, spectrum {spectrum:0.00}/s, caption {caption:0.00}/s, caption travel {distance / seconds:0.00} DIP/s.");
        Require(spectrum > 20 && caption > 20 && Math.Abs(distance / seconds - CaptionMarquee.Speed) <= 3,
            "Spectrum and glyph motion should render continuously at the expected travel speed.");
        return (spectrum, caption);
    }

    private static void CheckPixels(OverlayWindow overlay, OverlayLayoutSnapshot layout)
    {
        var dpi = VisualTreeHelper.GetDpi(overlay);
        var width = (int)Math.Ceiling(overlay.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(overlay.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(overlay);

        CheckRegion(layout.Caption!, "caption");
        CheckRegion(layout.Spectrum!, "spectrum");
        void CheckRegion(OverlayElementBounds bounds, string name)
        {
            var x = (int)Math.Floor(bounds.X * dpi.DpiScaleX);
            var y = (int)Math.Floor(bounds.Y * dpi.DpiScaleY);
            var columns = Math.Min(width - x, (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX));
            var rows = Math.Min(height - y, (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY));
            var bytes = new byte[columns * rows * 4];
            bitmap.CopyPixels(new Int32Rect(x, y, columns, rows), bytes, columns * 4, 0);
            int bright = 0;
            for (int pixel = 0; pixel < bytes.Length; pixel += 4)
            {
                if (bytes[pixel] > 100 && bytes[pixel + 1] > 100 && bytes[pixel + 3] > 100)
                    bright++;
            }
            Require(bright >= 10, $"The {name} region did not contain rendered pixels.");
            Console.WriteLine($"{name} crop {columns}x{rows} px has {bright} bright ink pixels.");
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        void Exit(object? sender, EventArgs args) => frame.Continue = false;
        timer.Tick += Exit;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally
        {
            timer.Stop();
            timer.Tick -= Exit;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
