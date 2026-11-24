using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Rifflo.Settings;

namespace Rifflo.Overlay;

internal sealed class CaptionPanel : FrameworkElement
{
    private readonly TranslateTransform _translation = new();
    private readonly DrawingGroup _positionedLine;
    private readonly OverlayFrameClock _frames;
    private readonly DispatcherTimer _wake;
    private readonly FrameTrace? _trace;
    private string _title = "";
    private string _artist = "";
    private string _source = "";
    private long _revision;
    private double _fontSize = 20;
    private double _targetWidth = 260;
    private double _viewportWidth;
    private Brush _foreground = Brushes.White;
    private CaptionScrollMode _mode;
    private CaptionDrawing? _line;
    private CaptionDrawing? _staticLine;
    private CaptionDrawing? _marqueeLine;
    private long _scrollStart;
    private bool _subscribed;
    private bool _restart;

    public CaptionPanel(OverlayFrameClock? frames = null, bool traceFrames = false)
    {
        _frames = frames ?? new OverlayFrameClock();
        _wake = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _wake.Tick += Wake;
        _trace = traceFrames ? new FrameTrace() : null;
        _positionedLine = new DrawingGroup { Transform = _translation };
        ClipToBounds = true;
        IsVisibleChanged += (_, _) => RestartScroll();
        Unloaded += (_, _) => StopScroll();
    }

    public double ContentWidth => _line?.Width ?? 0;
    public double Offset => -_translation.X;
    public bool IsScrolling => _subscribed || _wake.IsEnabled;
    public long OffsetCommitCount { get; private set; }
    public FrameSample[] GetFrames() => _trace?.Snapshot() ?? [];

    public void SetTrack(string title, string artist, string source = "", long revision = 0)
    {
        var trackChanged = _revision != revision || !string.Equals(_source, source, StringComparison.OrdinalIgnoreCase);
        _source = source;
        _revision = revision;
        if (_title == title && _artist == artist)
        {
            if (trackChanged)
                RestartScroll();
            return;
        }

        _title = title;
        _artist = artist;
        InvalidateLine(restart: true);
    }

    public void SetAppearance(double fontSize, SolidColorBrush foreground)
    {
        var fontChanged = _fontSize != fontSize;
        _fontSize = fontSize;
        _foreground = foreground;
        var color = foreground.Color;
        var luminance = color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722;
        var shadow = new DropShadowEffect
        {
            BlurRadius = 4,
            Color = luminance >= 130 ? Colors.Black : Colors.White,
            Opacity = 1,
            ShadowDepth = 0,
            RenderingBias = RenderingBias.Performance
        };
        shadow.Freeze();
        Effect = shadow;
        InvalidateLine(fontChanged);
    }

    public void SetViewport(double width, CaptionScrollMode mode)
    {
        if (_targetWidth != width)
        {
            _targetWidth = width;
            _restart = true;
            InvalidateMeasure();
        }

        if (_mode != mode)
        {
            _mode = mode;
            RestartScroll();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateLine(restart: true);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = Math.Min(_targetWidth, availableSize.Width);
        var height = SelectLine(width, pixelsPerDip);
        if (_restart)
        {
            _restart = false;
            RestartScroll();
        }
        return new Size(width, Math.Ceiling(height * pixelsPerDip) / pixelsPerDip);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_viewportWidth != finalSize.Width)
        {
            _viewportWidth = finalSize.Width;
            SelectLine(_viewportWidth, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            RestartScroll();
        }
        _translation.Y = Math.Max(0, (finalSize.Height - (_line?.Height ?? 0)) / 2);
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
            return;

        // Clip before the viewport's shadow effect so even very long content has a screen-sized render surface.
        drawing.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        drawing.DrawDrawing(_positionedLine);
        drawing.Pop();
    }

    private double SelectLine(double width, double pixelsPerDip)
    {
        _staticLine ??= CaptionDrawing.Create(_title, _artist, _fontSize, _foreground, pixelsPerDip);
        var line = _staticLine;
        if (width > 0 && line.Width > width + 1e-6)
        {
            // Display glyph origins snap to whole pixels. Ideal glyphs retain the fractions in
            // the existing translation, while captions that fit keep the static text rendering.
            line = _marqueeLine ??= CaptionDrawing.Create(_title, _artist, _fontSize, _foreground, pixelsPerDip,
                TextFormattingMode.Ideal).MatchLayout(_staticLine);
        }
        if (!ReferenceEquals(_line, line))
        {
            _line = line;
            _positionedLine.Children.Clear();
            _positionedLine.Children.Add(line.Drawing);
        }
        return line.Height;
    }

    private void InvalidateLine(bool restart)
    {
        _line = _staticLine = _marqueeLine = null;
        _restart |= restart;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void RestartScroll()
    {
        StopScroll();
        SetOffset(0);
        _scrollStart = Stopwatch.GetTimestamp();
        if (IsVisible && _viewportWidth > 0 && ContentWidth > _viewportWidth + 1e-6)
            ScheduleWake(CaptionMarquee.HoldSeconds);
    }

    private void StopScroll()
    {
        _wake.Stop();
        SetSubscribed(false);
    }

    private void ScheduleWake(double seconds)
    {
        SetSubscribed(false);
        _wake.Interval = TimeSpan.FromSeconds(seconds);
        _wake.Start();
    }

    private void Wake(object? sender, EventArgs args)
    {
        _wake.Stop();
        if (IsVisible)
            SetSubscribed(true);
    }

    private void SetSubscribed(bool subscribe)
    {
        if (_subscribed == subscribe)
            return;

        _subscribed = subscribe;
        if (subscribe)
            _frames.Tick += Scroll;
        else
            _frames.Tick -= Scroll;
    }

    private void Scroll(long now)
    {
        if (!_subscribed || !IsVisible)
            return;

        SetOffset(CaptionMarquee.GetOffset(ContentWidth - _viewportWidth, _mode,
            Stopwatch.GetElapsedTime(_scrollStart, now).TotalSeconds, out var holdSeconds));
        if (holdSeconds > 0)
            ScheduleWake(holdSeconds);
    }

    private void SetOffset(double value)
    {
        if (Offset == value)
            return;

        _translation.X = -value;
        if (IsVisible)
        {
            OffsetCommitCount++;
            _trace?.Record(value);
        }
    }
}
