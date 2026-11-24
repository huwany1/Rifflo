using System.Diagnostics;

namespace Rifflo.Overlay;

internal readonly record struct FrameSample(long Timestamp, double Value);

internal sealed class FrameTrace
{
    private readonly FrameSample[] _samples = new FrameSample[240];
    private int _next;
    private int _count;

    public void Record(double value)
    {
        _samples[_next] = new FrameSample(Stopwatch.GetTimestamp(), value);
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_count + 1, _samples.Length);
    }

    public FrameSample[] Snapshot()
    {
        var result = new FrameSample[_count];
        var start = (_next + _samples.Length - _count) % _samples.Length;
        var first = Math.Min(result.Length, _samples.Length - start);
        _samples.AsSpan(start, first).CopyTo(result);
        _samples.AsSpan(0, result.Length - first).CopyTo(result.AsSpan(first));
        return result;
    }
}
