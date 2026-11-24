using System.Windows;
using System.Windows.Controls;
using Rifflo.Settings;

namespace Rifflo.Overlay;

internal sealed class OverlayLayoutPanel : Panel
{
    private const double HorizontalGap = 12;
    private const double VerticalGap = 5;
    private readonly CaptionPanel _caption;
    private readonly SpectrumView _spectrum;
    private OverlayLayout _layout;
    private double _horizontalGap = HorizontalGap;

    public OverlayLayoutPanel(CaptionPanel caption, SpectrumView spectrum)
    {
        _caption = caption;
        _spectrum = spectrum;
        Children.Add(caption);
        Children.Add(spectrum);
    }

    public void SetLayout(OverlayLayout layout)
    {
        if (_layout == layout)
            return;

        _layout = layout;
        InvalidateMeasure();
    }

    internal static double WorkAreaContentWidth(WorkArea area, double horizontalInsets, bool reserveDefaultMargin)
    {
        var reservedPixels = reserveDefaultMargin ? PlacementMath.ToPixels(PlacementMath.MarginDip, area.Dpi) : 0;
        var reservedWidth = reservedPixels * 96.0 / area.Dpi;
        // Keep one DIP for rounding the window chrome and content to physical pixels.
        return Math.Max(0, area.WidthDip - reservedWidth - horizontalInsets - 1);
    }

    private bool IsHorizontal => _layout is OverlayLayout.CaptionLeft or OverlayLayout.SpectrumLeft;
    private bool HasSpectrum => _spectrum.Visibility == Visibility.Visible;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!HasSpectrum)
        {
            _caption.Measure(availableSize);
            return _caption.DesiredSize;
        }

        var width = availableSize.Width;
        _horizontalGap = Math.Min(HorizontalGap, width / 3);
        // Give the caption room for a glyph even when the working area is very narrow.
        var captionMinimum = Math.Min(48, width / 2);
        var spectrumWidth = IsHorizontal ? Math.Max(0, width - _horizontalGap - captionMinimum) : width;
        _spectrum.Measure(new Size(spectrumWidth, availableSize.Height));
        var spectrum = _spectrum.DesiredSize;
        var captionWidth = IsHorizontal ? Math.Max(0, width - _horizontalGap - spectrum.Width) : width;
        _caption.Measure(new Size(captionWidth, availableSize.Height));
        var caption = _caption.DesiredSize;

        return IsHorizontal
            ? new Size(caption.Width + _horizontalGap + spectrum.Width, Math.Max(caption.Height, spectrum.Height))
            : new Size(Math.Max(caption.Width, spectrum.Width), caption.Height + VerticalGap + spectrum.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var caption = _caption.DesiredSize;
        if (!HasSpectrum)
        {
            _caption.Arrange(new Rect(new Point(0, 0), caption));
            return finalSize;
        }

        var spectrum = _spectrum.DesiredSize;
        var captionFirst = _layout is OverlayLayout.CaptionAbove or OverlayLayout.CaptionLeft;
        var (first, second) = captionFirst ? ((UIElement)_caption, (UIElement)_spectrum) : (_spectrum, _caption);
        var firstSize = first.DesiredSize;
        var secondSize = second.DesiredSize;
        if (IsHorizontal)
        {
            first.Arrange(new Rect(0, finalSize.Height - firstSize.Height, firstSize.Width, firstSize.Height));
            second.Arrange(new Rect(firstSize.Width + _horizontalGap, finalSize.Height - secondSize.Height, secondSize.Width, secondSize.Height));
        }
        else
        {
            first.Arrange(new Rect(new Point(0, 0), firstSize));
            second.Arrange(new Rect(new Point(0, firstSize.Height + VerticalGap), secondSize));
        }
        return finalSize;
    }
}
