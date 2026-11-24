using System.Windows;
using System.Windows.Media;

namespace Rifflo.Overlay;

internal sealed class SpectrumView : FrameworkElement
{
    private const double HeightDip = 27;
    private static readonly Pen DarkOutline = CreateOutline(Brushes.Black);
    private static readonly Pen LightOutline = CreateOutline(Brushes.White);
    private readonly DrawingGroup _bars = new();
    private readonly FrameTrace? _trace;
    private float[] _levels = new float[16];
    private double[] _renderedBarHeights = new double[16];
    private Brush _brush = Brushes.LightBlue;
    private Pen _outline = DarkOutline;
    private double _renderedWidth;
    private double _renderedHeight;
    private bool _geometryDirty = true;
    private bool _appearanceDirty = true;

    public SpectrumView(bool traceFrames = false)
    {
        _trace = traceFrames ? new FrameTrace() : null;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                UpdateBars();
        };
    }

    public long DrawCount { get; private set; }
    public long ChangedCount { get; private set; }
    public ReadOnlySpan<double> RenderedBarHeights => _renderedBarHeights;
    public FrameSample[] GetFrames() => _trace?.Snapshot() ?? [];

    public void SetAppearance(int count, Brush brush)
    {
        if (_levels.Length != count)
        {
            _levels = new float[count];
            _renderedBarHeights = new double[count];
            _geometryDirty = true;
            InvalidateMeasure();
        }
        _brush = brush;
        var color = ((SolidColorBrush)brush).Color;
        var luminance = color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722;
        _outline = luminance >= 130 ? DarkOutline : LightOutline;
        _appearanceDirty = true;
        UpdateBars();
    }

    public void SetBands(ReadOnlySpan<float> levels)
    {
        for (var i = 0; i < _levels.Length; i++)
        {
            var level = i < levels.Length && float.IsFinite(levels[i]) ? Math.Clamp(levels[i], 0, 1) : 0;
            if (Math.Abs(level - _levels[i]) < 0.004 && (level != 0 || _levels[i] == 0))
                continue;

            _levels[i] = level;
            _geometryDirty = true;
        }
        UpdateBars();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(availableSize.Width, _levels.Length * 8 - 3), HeightDip);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        UpdateBars();
        drawing.DrawDrawing(_bars);
    }

    private void UpdateBars()
    {
        var viewWidth = ActualWidth;
        var viewHeight = ActualHeight;
        var resized = viewWidth != _renderedWidth || viewHeight != _renderedHeight;
        if (viewWidth <= 0 || viewHeight <= 0 || (IsLoaded && !IsVisible) ||
            (!resized && !_geometryDirty && !_appearanceDirty))
            return;

        var gap = Math.Min(3, viewWidth / (2 * _levels.Length));
        var stride = (viewWidth + gap) / _levels.Length;
        var width = stride - gap;
        var outline = width > _outline.Thickness ? _outline : null;
        var inset = (outline?.Thickness ?? 0) / 2;
        var minimum = Math.Min(2, viewHeight);
        if (_bars.Children.Count != _levels.Length)
        {
            _bars.Children.Clear();
            for (var i = 0; i < _levels.Length; i++)
                _bars.Children.Add(new GeometryDrawing(_brush, outline, new RectangleGeometry(Rect.Empty, 1.6, 1.6)));
        }

        var heightsChanged = false;
        var meanHeight = 0.0;
        for (var i = 0; i < _levels.Length; i++)
        {
            var height = minimum + _levels[i] * (viewHeight - minimum);
            var heightChanged = _renderedBarHeights[i] != height;
            _renderedBarHeights[i] = height;
            heightsChanged |= heightChanged;
            meanHeight += height;

            var bar = (GeometryDrawing)_bars.Children[i];
            if (_appearanceDirty || resized)
            {
                bar.Brush = _brush;
                bar.Pen = outline;
            }
            if (heightChanged || resized || _appearanceDirty)
            {
                var geometry = (RectangleGeometry)bar.Geometry;
                geometry.Rect = new Rect(i * stride + inset, viewHeight - height + inset,
                    width - 2 * inset, Math.Max(0, height - 2 * inset));
            }
        }

        _renderedWidth = viewWidth;
        _renderedHeight = viewHeight;
        _geometryDirty = _appearanceDirty = false;
        DrawCount++;
        if (!heightsChanged)
            return;

        ChangedCount++;
        _trace?.Record(meanHeight / _levels.Length);
    }

    private static Pen CreateOutline(Brush brush)
    {
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }
}
