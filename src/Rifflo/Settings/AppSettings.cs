using System.Text.Json.Serialization;

namespace Rifflo.Settings;

public sealed record OverlayPosition(int X, int Y)
{
    [JsonConverter(typeof(MonitorIdSettingConverter))]
    public string? MonitorId { get; init; }
}

public enum OverlayLayout
{
    SpectrumAbove,
    CaptionAbove,
    CaptionLeft,
    SpectrumLeft
}

public enum CaptionScrollMode
{
    Loop,
    Bounce
}

public sealed record AppSettings
{
    public const int DefaultAnimationFrameRate = 30;
    public const int HighAnimationFrameRate = 60;
    public const double MinimumTextWidth = 160;
    public const double MaximumTextWidth = 640;
    public static AppSettings Defaults { get; } = new();

    public bool ShowArtist { get; init; } = true;
    public double FontSize { get; init; } = 20;
    public string TextColor { get; init; } = "#F5F7FA";
    public string BarColor { get; init; } = "#83BAFF";
    public double Opacity { get; init; } = 0.95;
    public int BarCount { get; init; } = 16;
    public bool SpectrumEnabled { get; init; } = true;
    [JsonConverter(typeof(LiveCaptureEnabledSettingConverter))]
    public bool LiveCaptureEnabled { get; init; } = true;
    public double Sensitivity { get; init; } = 1;
    [JsonConverter(typeof(LayoutSettingConverter))]
    public OverlayLayout Layout { get; init; } = OverlayLayout.SpectrumAbove;
    [JsonConverter(typeof(TextWidthSettingConverter))]
    public double TextWidth { get; init; } = 260;
    [JsonConverter(typeof(ScrollModeSettingConverter))]
    public CaptionScrollMode ScrollMode { get; init; } = CaptionScrollMode.Loop;
    [JsonConverter(typeof(AnimationFrameRateSettingConverter))]
    public int AnimationFrameRate { get; init; } = DefaultAnimationFrameRate;
    public OverlayPosition? Position { get; init; }
    [JsonConverter(typeof(PositionLockedSettingConverter))]
    public bool PositionLocked { get; init; }
    public bool Topmost { get; init; } = true;
    [JsonConverter(typeof(BroadcastCaptureEnabledSettingConverter))]
    public bool BroadcastCaptureEnabled { get; init; }
    public string PreferredSource { get; init; } = "";

    public AppSettings Normalize() => this with
    {
        FontSize = Clamp(FontSize, 12, 36, Defaults.FontSize),
        TextColor = NormalizeColor(TextColor, Defaults.TextColor),
        BarColor = NormalizeColor(BarColor, Defaults.BarColor),
        Opacity = Clamp(Opacity, 0.2, 1, Defaults.Opacity),
        BarCount = Math.Clamp(BarCount, 8, 32),
        Sensitivity = Clamp(Sensitivity, 0.25, 3, Defaults.Sensitivity),
        Layout = Enum.IsDefined(Layout) ? Layout : Defaults.Layout,
        TextWidth = Clamp(TextWidth, MinimumTextWidth, MaximumTextWidth, Defaults.TextWidth),
        ScrollMode = Enum.IsDefined(ScrollMode) ? ScrollMode : Defaults.ScrollMode,
        AnimationFrameRate = AnimationFrameRate == HighAnimationFrameRate ? HighAnimationFrameRate : DefaultAnimationFrameRate,
        Position = Position is { MonitorId: { } monitorId } && string.IsNullOrWhiteSpace(monitorId)
            ? Position with { MonitorId = null }
            : Position,
        PreferredSource = PreferredSource?.Trim() ?? ""
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string NormalizeColor(string? value, string fallback)
    {
        if (value is not { Length: 7 } || value[0] != '#')
            return fallback;

        foreach (var character in value.AsSpan(1))
            if (!Uri.IsHexDigit(character))
                return fallback;

        return value.ToUpperInvariant();
    }
}
