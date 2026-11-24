using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Dsp;

namespace Rifflo.Audio;

internal sealed class SpectrumAnalyzer
{
    public const int Size = 4096;
    public const int SampleRate = 44100;
    public const int DetailBands = 96;
    public static readonly TimeSpan PacketGap = TimeSpan.FromMilliseconds(170);
    public static readonly TimeSpan VisibleTail = TimeSpan.FromMilliseconds(600);

    private const int HopSize = Size / 2;
    private const int HistorySize = 16;
    private const float SignalFloor = 0.00055f;
    private readonly FftProcessor fft = new(Size, FftWindowType.Hann);
    private readonly Complex[] leftFft = new Complex[Size / 2 + 1];
    private readonly Complex[] rightFft = new Complex[Size / 2 + 1];
    private readonly float[] leftRing = new float[Size];
    private readonly float[] rightRing = new float[Size];
    private readonly float[] window = new float[Size];
    private readonly float[] bands = new float[HistorySize * DetailBands];
    private readonly long[] windowTimes = new long[HistorySize];
    private readonly int[] edges = BuildEdges();
    private int writeIndex;
    private int untilWindow = Size;
    private int version;
    private long windowSequence;
    private long firstWindowSequence = 1;
    private long lastPacketAt;
    private long lastWindowAt;
    private long lastSignalAt;
    private float lastPeak;
    private int peakBand;

    public long WindowSequence => Volatile.Read(ref windowSequence);
    public int PeakBand => Volatile.Read(ref peakBand);

    public void Reset()
    {
        Interlocked.Increment(ref version);
        writeIndex = 0;
        untilWindow = Size;
        lastPacketAt = 0;
        Volatile.Write(ref firstWindowSequence, windowSequence + 1);
        Volatile.Write(ref lastWindowAt, 0);
        Volatile.Write(ref lastSignalAt, 0);
        Volatile.Write(ref lastPeak, 0);
        Interlocked.Increment(ref version);
    }

    public int Push(ReadOnlySpan<byte> buffer, bool silent, bool discontinuity, long now)
    {
        var samples = MemoryMarshal.Cast<byte, short>(buffer);
        var frames = samples.Length / 2;
        if (frames == 0)
            return 0;

        if (discontinuity || frames > Size * 2 || lastPacketAt == 0 ||
            Stopwatch.GetElapsedTime(lastPacketAt, now) > PacketGap)
        {
            writeIndex = 0;
            untilWindow = Size;
        }

        lastPacketAt = now;
        int windows = 0;
        for (int frame = Math.Max(0, frames - Size * 2); frame < frames; frame++)
        {
            leftRing[writeIndex] = silent ? 0 : samples[frame * 2] / 32768f;
            rightRing[writeIndex] = silent ? 0 : samples[frame * 2 + 1] / 32768f;
            writeIndex = (writeIndex + 1) & (Size - 1);
            if (--untilWindow != 0)
                continue;

            Analyze(now);
            untilWindow = HopSize;
            windows++;
        }

        return windows;
    }

    private void Analyze(long now)
    {
        CopyRing(leftRing);
        fft.RealForward(window, leftFft);
        CopyRing(rightRing);
        fft.RealForward(window, rightFft);

        float strongest = 0;
        int strongestBand = 0;
        long sequence = windowSequence + 1;
        int slot = (int)(sequence % HistorySize);
        var frame = bands.AsSpan(slot * DetailBands, DetailBands);
        Interlocked.Increment(ref version);
        for (int band = 0; band < DetailBands; band++)
        {
            float power = 0;
            for (int bin = edges[band]; bin < Math.Max(edges[band] + 1, edges[band + 1]); bin++)
            {
                var left = leftFft[bin];
                var right = rightFft[bin];
                power = Math.Max(power, (left.X * left.X + left.Y * left.Y +
                    right.X * right.X + right.Y * right.Y) * 0.5f);
            }

            // The normalized real FFT returns half of a sine's peak, and Hann halves it again.
            var amplitude = MathF.Sqrt(power) * 4;
            frame[band] = amplitude;
            if (amplitude > strongest)
            {
                strongest = amplitude;
                strongestBand = band;
            }
        }

        windowTimes[slot] = now;
        Volatile.Write(ref lastPeak, strongest);
        Volatile.Write(ref peakBand, strongestBand);
        Volatile.Write(ref lastWindowAt, now);
        Volatile.Write(ref windowSequence, sequence);
        if (strongest >= SignalFloor)
            Volatile.Write(ref lastSignalAt, now);
        Interlocked.Increment(ref version);
    }

    private void CopyRing(float[] ring)
    {
        ring.AsSpan(writeIndex).CopyTo(window);
        ring.AsSpan(0, writeIndex).CopyTo(window.AsSpan(Size - writeIndex));
    }

    public bool HasSignal(long now)
    {
        var last = Volatile.Read(ref lastSignalAt);
        return last != 0 && Stopwatch.GetElapsedTime(last, now) < VisibleTail;
    }

    public bool HasDisplaySignal(float threshold, long now)
    {
        var last = Volatile.Read(ref lastWindowAt);
        return last != 0 && Stopwatch.GetElapsedTime(last, now) <= PacketGap && Volatile.Read(ref lastPeak) >= threshold;
    }

    public bool TryRead(Span<float> destination, long now)
    {
        long sequence = 0;
        return TryReadFrame(destination, ref sequence, now, latestOnly: true, out _);
    }

    public bool TryReadNext(Span<float> destination, ref long sequence, long now, out long timestamp) =>
        TryReadFrame(destination, ref sequence, now, latestOnly: false, out timestamp);

    private bool TryReadFrame(Span<float> destination, ref long sequence, long now, bool latestOnly, out long timestamp)
    {
        timestamp = 0;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var before = Volatile.Read(ref version);
            if ((before & 1) != 0)
                continue;

            var head = Volatile.Read(ref windowSequence);
            var first = Volatile.Read(ref firstWindowSequence);
            if (sequence >= head || head < first)
                return false;

            var next = latestOnly ? head : Math.Max(first, Math.Max(sequence + 1, head - HistorySize + 1));
            var slot = (int)(next % HistorySize);
            var at = windowTimes[slot];
            if (at > now || (latestOnly && Stopwatch.GetElapsedTime(at, now) > PacketGap))
                return false;

            bands.AsSpan(slot * DetailBands, DetailBands).CopyTo(destination);
            // Pair with the writer's interlocked release and keep the copy inside the version check.
            if (before == Interlocked.CompareExchange(ref version, 0, 0))
            {
                timestamp = at;
                sequence = next;
                return true;
            }
        }

        return false;
    }

    private static int[] BuildEdges()
    {
        const double lowest = 45;
        const double highest = 12500;
        var result = new int[DetailBands + 1];
        for (int band = 0; band <= DetailBands; band++)
            result[band] = (int)Math.Ceiling(lowest * Math.Pow(highest / lowest, (double)band / DetailBands) * Size / SampleRate);
        return result;
    }
}

internal sealed class SpectrumEnvelope(SpectrumAnalyzer analyzer)
{
    private const float VisibilityFloor = 0.003f;
    private static readonly long PacketGapTicks = (long)(SpectrumAnalyzer.PacketGap.TotalSeconds * Stopwatch.Frequency);
    private readonly float[] detail = new float[SpectrumAnalyzer.DetailBands];
    private readonly float[] nextDetail = new float[SpectrumAnalyzer.DetailBands];
    private float[] values = [];
    private float[] targets = [];
    private float gain;
    private long sequence;
    private long anchorAt;
    private long targetAt;
    private long lastReadAt;
    private bool hasVisibleTarget;

    public long ConsumedWindowSequence => sequence;

    public void Suspend(long now)
    {
        // Start the release at the last evaluated heights, discarding unapplied FFT targets.
        if (lastReadAt != 0)
            AdvanceTo(Math.Min(lastReadAt, now));
        targetAt = 0;
        sequence = analyzer.WindowSequence;
        hasVisibleTarget = false;
        Array.Clear(detail);
        Array.Clear(targets);
    }

    public bool Read(Span<float> destination, double sensitivity, long now, bool permitSignal)
    {
        if (values.Length != destination.Length)
        {
            values = new float[destination.Length];
            targets = new float[destination.Length];
            anchorAt = targetAt = sequence = lastReadAt = 0;
            hasVisibleTarget = false;
        }
        if (destination.Length == 0)
            return false;

        var currentGain = GetGain(sensitivity);
        if (!permitSignal)
        {
            if (targetAt != 0)
            {
                AdvanceTo(now);
                targetAt = 0;
            }
            sequence = analyzer.WindowSequence;
        }
        else
        {
            if (gain != currentGain && targetAt != 0)
            {
                AdvanceTo(now);
                SetTargets(currentGain);
            }
            while (analyzer.TryReadNext(nextDetail, ref sequence, now, out var timestamp))
            {
                if (anchorAt == 0)
                {
                    // Preserve the original initial attack as capture first becomes visible.
                    anchorAt = timestamp - Stopwatch.Frequency / 30;
                    targetAt = anchorAt;
                    nextDetail.AsSpan().CopyTo(detail);
                    SetTargets(currentGain);
                }

                AdvanceTo(timestamp);
                targetAt = timestamp;
                nextDetail.AsSpan().CopyTo(detail);
                SetTargets(currentGain);
            }
        }

        gain = currentGain;
        lastReadAt = now;
        return Sample(destination, now, applyFloor: true) ||
            (targetAt != 0 && now < targetAt + PacketGapTicks && hasVisibleTarget);
    }

    public static float DisplayThreshold(double sensitivity) =>
        MathF.Pow(10, (VisibilityFloor * 49 - 55) / 20) / GetGain(sensitivity);

    private static float GetGain(double sensitivity) =>
        double.IsFinite(sensitivity) ? (float)Math.Clamp(sensitivity, 0.05, 10) : 1;

    private void AdvanceTo(long timestamp)
    {
        if (timestamp <= anchorAt)
            return;
        Sample(values, timestamp, applyFloor: false);
        anchorAt = timestamp;
    }

    private bool Sample(Span<float> destination, long timestamp, bool applyFloor)
    {
        // Commit the envelope only at input changes. Every visual sample evaluates the same
        // attack/release curve, even when a frame is skipped or the frame rate changes.
        var now = Math.Max(anchorAt, timestamp);
        var freshUntil = targetAt == 0 ? anchorAt : Math.Clamp(targetAt + PacketGapTicks, anchorAt, now);
        var fresh = Stopwatch.GetElapsedTime(anchorAt, freshUntil).TotalSeconds;
        var attack = (float)Math.Exp(-fresh / 0.022);
        var release = (float)Math.Exp(-fresh / 0.17);
        var quiet = (float)Math.Exp(-Stopwatch.GetElapsedTime(freshUntil, now).TotalSeconds / 0.17);
        var active = false;
        for (int band = 0; band < destination.Length; band++)
        {
            var level = values[band];
            var target = targets[band];
            level = (target + (level - target) * (target > level ? attack : release)) * quiet;
            destination[band] = applyFloor && level < VisibilityFloor ? 0 : level;
            active |= destination[band] > 0;
        }

        return active;
    }

    private void SetTargets(float currentGain)
    {
        hasVisibleTarget = false;
        for (int band = 0; band < targets.Length; band++)
        {
            float peak = 0;
            int first = band * detail.Length / targets.Length;
            int last = Math.Max(first + 1, (band + 1) * detail.Length / targets.Length);
            for (int index = first; index < last; index++)
                peak = Math.Max(peak, detail[index]);

            var magnitude = peak == 0 ? 0 : Math.Clamp((20 * MathF.Log10(peak * currentGain) + 55) / 49, 0, 1);
            targets[band] = magnitude;
            hasVisibleTarget |= magnitude >= VisibilityFloor;
        }
    }
}
