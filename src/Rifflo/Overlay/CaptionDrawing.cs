using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rifflo.Overlay;

internal sealed record CaptionDrawing(DrawingGroup Drawing, double Width, double Height)
{
    // WPF's line formatter limits the number of UTF-16 code units in a single line.
    private const int FormattingChunkLength = 8192;
    private const double SeparatorPadding = 6;
    private static readonly FontFamily Family = new("Segoe UI");

    public static CaptionDrawing Create(string title, string artist, double fontSize, Brush color, double pixelsPerDip,
        TextFormattingMode formattingMode = TextFormattingMode.Display)
    {
        var runs = new List<(FormattedText Text, double X)>();
        var advance = 0.0;
        var left = 0.0;
        var right = 0.0;
        var ascent = 0.0;
        var descent = 0.0;

        if (title.Length > 0)
            AddField(title, FontWeights.SemiBold, color);

        if (artist.Length > 0)
        {
            advance += SeparatorPadding;
            AddField("·", FontWeights.Normal, WithOpacity(color, 0.78));
            advance += SeparatorPadding;
            AddField(artist, FontWeights.Normal, WithOpacity(color, 0.85));
        }

        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            foreach (var (text, x) in runs)
                context.DrawText(text, new Point(x - left, ascent - text.Baseline));
        }
        drawing.Freeze();
        return new CaptionDrawing(drawing, Math.Max(right, advance) - left, ascent + descent);

        void AddField(string value, FontWeight weight, Brush foreground)
        {
            var typeface = new Typeface(Family, FontStyles.Normal, weight, FontStretches.Normal);
            for (var start = 0; start < value.Length;)
            {
                var length = GetChunkLength(value.AsSpan(start));
                var text = new FormattedText(value.Substring(start, length), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, fontSize, foreground, null,
                    formattingMode, pixelsPerDip) { Trimming = TextTrimming.None };

                runs.Add((text, advance));
                left = Math.Min(left, advance + text.OverhangLeading);
                right = Math.Max(right, advance + text.Width - text.OverhangTrailing);
                advance += text.WidthIncludingTrailingWhitespace;
                var inkBottom = text.Height + text.OverhangAfter;
                var inkTop = inkBottom - text.Extent;
                ascent = Math.Max(ascent, text.Baseline - Math.Min(0, inkTop));
                descent = Math.Max(descent, Math.Max(text.Height, inkBottom) - text.Baseline);
                start += length;
            }
        }
    }

    public CaptionDrawing MatchLayout(CaptionDrawing reference)
    {
        if (Width <= 0 || Height <= 0)
            return reference;

        // Keep the established viewport and scroll endpoint for either set of glyph metrics.
        var drawing = new DrawingGroup
        {
            Transform = new ScaleTransform(reference.Width / Width, reference.Height / Height)
        };
        drawing.Children.Add(Drawing);
        drawing.Freeze();
        return reference with { Drawing = drawing };
    }

    private static int GetChunkLength(ReadOnlySpan<char> value)
    {
        if (value.Length <= FormattingChunkLength)
            return value.Length;

        // Keep surrogate pairs, combining marks and emoji sequences together at ordinary chunk boundaries.
        var end = 0;
        while (end < FormattingChunkLength)
        {
            var length = StringInfo.GetNextTextElementLength(value[end..]);
            if (end + length > FormattingChunkLength)
                break;
            end += length;
        }
        if (end > 0)
            return end;

        // An individual grapheme can exceed the formatter limit. Keep the entire input in scalar-aligned pieces.
        return char.IsHighSurrogate(value[FormattingChunkLength - 1]) ? FormattingChunkLength - 1 : FormattingChunkLength;
    }

    private static Brush WithOpacity(Brush color, double opacity)
    {
        var brush = color.Clone();
        brush.Opacity = opacity;
        brush.Freeze();
        return brush;
    }
}
