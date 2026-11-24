namespace Rifflo.Overlay;

internal static class SpectrumAnimation
{
    public const double PresetPeriodSeconds = 8;
    private const double ReleaseSeconds = 0.25;

    public static void FillPreset(Span<float> levels, double elapsedSeconds, double sensitivity)
    {
        var phase = Math.Tau * (elapsedSeconds % PresetPeriodSeconds) / PresetPeriodSeconds;
        var bassPulse = Math.Pow(0.5 + 0.5 * Math.Cos(phase * 14 + 0.25 * Math.Sin(phase * 2)), 3);
        var backbeat = Math.Pow(0.5 + 0.5 * Math.Sin(phase * 9 - 0.3 * Math.Cos(phase * 3)), 2);
        for (var i = 0; i < levels.Length; i++)
        {
            var band = (i + 0.5) / levels.Length;
            var ripple = 0.5 + 0.5 * Math.Sin(phase * 4 + band * 13 + 0.65 * Math.Sin(phase * 7 - band * 20));
            var bass = (0.14 + 0.39 * bassPulse) * Math.Exp(-3.8 * band);
            var mids = (0.06 + 0.24 * backbeat) * Math.Exp(-13 * (band - 0.45) * (band - 0.45));
            var lift = ripple * (0.11 + 0.08 * Math.Cos(band * Math.Tau + phase));
            levels[i] = (float)Math.Clamp((0.08 + bass + mids + lift) * Math.Sqrt(sensitivity), 0, 1);
        }
    }

    public static bool FadeToSilence(Span<float> levels, double elapsedSeconds)
    {
        var retention = (float)Math.Exp(-elapsedSeconds / ReleaseSeconds);
        var active = false;
        for (var i = 0; i < levels.Length; i++)
        {
            var level = levels[i] * retention;
            levels[i] = level >= 0.003f ? level : 0;
            active |= levels[i] > 0;
        }

        return active;
    }
}
