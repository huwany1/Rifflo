using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Rifflo.Settings;

namespace Rifflo.Overlay;

internal sealed class OverlayFrameClock : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _fallback;
    private readonly Action _dispatchTick;
    private FrameCadence _cadence = new(AppSettings.DefaultAnimationFrameRate);
    private Action<long>? _tick;
    private WaitableFrameTimer? _timer;
    private FrameTrace? _trace;
    private int _dispatchPending;
    private bool _collectDiagnostics;
    private bool _timerCreated;
    private bool _disposed;

    public OverlayFrameClock()
    {
        _dispatchTick = DispatchTick;
        _fallback = new DispatcherTimer(DispatcherPriority.Render, _dispatcher);
        _fallback.Tick += FallbackTick;
    }

    public bool IsRunning => _tick is not null;
    public long AnimationTickCount { get; private set; }

    public void EnableDiagnostics(bool traceFrames)
    {
        _collectDiagnostics = true;
        if (traceFrames)
            _trace = new FrameTrace();
    }

    public FrameSample[] GetFrames() => _trace?.Snapshot() ?? [];

    public event Action<long> Tick
    {
        add
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var wasIdle = _tick is null;
            _tick += value;
            if (!wasIdle)
                return;

            _cadence.Reset();
            if (!_timerCreated)
            {
                _timerCreated = true;
                _timer = WaitableFrameTimer.TryCreate(SignalDispatcher);
            }
            ScheduleNext();
        }
        remove
        {
            _tick -= value;
            if (_tick is null)
                StopTimer();
        }
    }

    public void SetFrameRate(int frameRate)
    {
        _cadence.SetFrameRate(frameRate);
        ScheduleNext();
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        _tick = null;
        StopTimer();
        _timer?.Dispose();
        _timer = null;
    }

    private void ScheduleNext()
    {
        if (_tick is null || Volatile.Read(ref _dispatchPending) != 0)
            return;

        var delay = _cadence.GetDelay(Stopwatch.GetTimestamp());
        if (_timer?.Arm(delay) == true)
            return;

        _timer?.Dispose();
        _timer = null;
        _fallback.Interval = delay;
        _fallback.Start();
    }

    private void StopTimer()
    {
        _timer?.Cancel();
        _fallback.Stop();
    }

    private void SignalDispatcher(object? state, bool timedOut)
    {
        if (Volatile.Read(ref _disposed) || Volatile.Read(ref _tick) is null ||
            _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _dispatchPending, 1) != 0)
            return;

        _dispatcher.InvokeAsync(_dispatchTick, DispatcherPriority.Render);
    }

    private void FallbackTick(object? sender, EventArgs args)
    {
        _fallback.Stop();
        DispatchTick();
    }

    private void DispatchTick()
    {
        // Schedule only after the UI has consumed this signal. A stalled dispatcher never
        // accumulates frame work, and retained WPF resources stay owned by the UI thread.
        try
        {
            var now = Stopwatch.GetTimestamp();
            if (_tick is null || !_cadence.TryAdvance(now))
                return;

            if (_collectDiagnostics)
            {
                AnimationTickCount++;
                _trace?.Record(0);
            }
            _tick.Invoke(now);
        }
        finally
        {
            Volatile.Write(ref _dispatchPending, 0);
            ScheduleNext();
        }
    }

    private sealed class WaitableFrameTimer : WaitHandle
    {
        private const uint HighResolution = 0x2;
        private const uint ModifyStateAndSynchronize = 0x00100002;
        private readonly RegisteredWaitHandle _wait;

        private WaitableFrameTimer(SafeWaitHandle handle, WaitOrTimerCallback onSignal)
        {
            SafeWaitHandle = handle;
            _wait = ThreadPool.RegisterWaitForSingleObject(this, onSignal, null, Timeout.Infinite, executeOnlyOnce: false);
        }

        public static WaitableFrameTimer? TryCreate(WaitOrTimerCallback onSignal)
        {
            var handle = CreateWaitableTimerEx(0, 0, HighResolution, ModifyStateAndSynchronize);
            if (!handle.IsInvalid)
                return new WaitableFrameTimer(handle, onSignal);

            handle.Dispose();
            return null;
        }

        public bool Arm(TimeSpan delay)
        {
            var dueTime = -delay.Ticks;
            return SetWaitableTimer(SafeWaitHandle, ref dueTime, 0, 0, 0, false);
        }

        public void Cancel() => CancelWaitableTimer(SafeWaitHandle);

        protected override void Dispose(bool explicitDisposing)
        {
            if (explicitDisposing)
                _wait.Unregister(null);
            base.Dispose(explicitDisposing);
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
        private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle handle, ref long dueTime, int period,
            nint completion, nint state, [MarshalAs(UnmanagedType.Bool)] bool resume);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelWaitableTimer(SafeWaitHandle handle);
    }
}

internal struct FrameCadence
{
    // Absorb small scheduling errors around the nominal frame boundary.
    private static readonly double BoundaryTolerance = Stopwatch.Frequency / 1000.0;
    private double _interval;
    private double _nextFrame;
    private long _lastFrame;
    private bool _started;

    public FrameCadence(int frameRate) => SetFrameRate(frameRate);

    public void SetFrameRate(int frameRate)
    {
        var interval = (double)Stopwatch.Frequency / (frameRate == AppSettings.HighAnimationFrameRate
            ? AppSettings.HighAnimationFrameRate : AppSettings.DefaultAnimationFrameRate);
        if (_interval == interval)
            return;

        _interval = interval;
        if (_started)
            _nextFrame = _lastFrame + interval;
    }

    public void Reset() => _started = false;

    public TimeSpan GetDelay(long timestamp)
    {
        var ticks = _started ? Math.Ceiling((_nextFrame - timestamp) * TimeSpan.TicksPerSecond / Stopwatch.Frequency) : 0;
        return TimeSpan.FromTicks((long)Math.Max(1, ticks));
    }

    public bool TryAdvance(long timestamp)
    {
        // A duplicate callback cannot consume a second deadline.
        if (_started && (timestamp <= _lastFrame || timestamp + BoundaryTolerance < _nextFrame))
            return false;

        // Keep fractional deadlines under normal scheduling jitter. A late frame starts a fresh
        // interval once following the previous deadline would produce an unusually short gap.
        _nextFrame = !_started || timestamp >= _nextFrame + _interval / 2
            ? timestamp + _interval : _nextFrame + _interval;
        _lastFrame = timestamp;
        _started = true;
        return true;
    }
}
