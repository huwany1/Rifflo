using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rifflo.Settings;

namespace Rifflo.Overlay;

internal sealed class OverlayDragController
{
    private readonly Window _window;
    private readonly DispatcherTimer _sample;
    private DragStart? _start;
    private nint _monitor;
    private DesktopWindow.Point _pointer;
    private OverlayPosition? _position;

    internal OverlayDragController(Window window)
    {
        _window = window;
        _sample = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _sample.Tick += (_, _) => Sample();
        window.PreviewMouseLeftButtonDown += Begin;
        window.PreviewMouseMove += Drag;
        window.PreviewMouseLeftButtonUp += Release;
        window.Closed += (_, _) => Stop();
    }

    internal bool Enabled { get; set; } = true;
    internal bool IsDragging => _start is not null;
    internal WorkArea? DragWorkArea { get; private set; }
    internal event Action? WorkAreaChanged;
    internal event Action<OverlayPosition>? Completed;

    internal void Cancel()
    {
        var start = _start;
        Stop();
        DragWorkArea = null;
        if (start is not null)
            DesktopWindow.Move(start.Handle, start.Left, start.Top);
    }

    private void Begin(object sender, MouseButtonEventArgs args)
    {
        if (!Enabled || IsDragging || !DesktopWindow.GetCursorPos(out var pointer))
            return;

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == 0 || !DesktopWindow.GetWindowRect(handle, out var bounds))
            return;

        var dpi = VisualTreeHelper.GetDpi(_window);
        _start = new DragStart(handle, bounds.Left, bounds.Top,
            (pointer.X - bounds.Left) / dpi.DpiScaleX, (pointer.Y - bounds.Top) / dpi.DpiScaleY);
        _pointer = pointer;
        _monitor = DesktopWindow.MonitorAt(pointer);
        DragWorkArea = DesktopWindow.GetWorkArea(_monitor);
        _position = null;

        // A non-activating window can lose mouse messages when the pointer leaves its bounds.
        _window.CaptureMouse();
        _sample.Start();
        args.Handled = true;
    }

    private void Drag(object sender, MouseEventArgs args)
    {
        if (!IsDragging || args.LeftButton != MouseButtonState.Pressed)
            return;
        Move();
        args.Handled = true;
    }

    private void Release(object sender, MouseButtonEventArgs args)
    {
        if (!IsDragging)
            return;
        Complete();
        args.Handled = true;
    }

    private void Sample()
    {
        if (!IsDragging)
            return;
        if (DesktopWindow.IsPrimaryButtonDown())
            Move();
        else
            Complete();
    }

    private void Move()
    {
        if (_start is not { } start || !DesktopWindow.GetCursorPos(out var pointer) ||
            (pointer.X == _pointer.X && pointer.Y == _pointer.Y))
            return;

        _pointer = pointer;
        var monitor = DesktopWindow.MonitorAt(pointer);
        var crossed = _monitor != monitor;
        if (crossed)
        {
            _monitor = monitor;
            DragWorkArea = DesktopWindow.GetWorkArea(monitor);
        }

        var dpi = DragWorkArea?.Dpi ?? (uint)VisualTreeHelper.GetDpi(_window).PixelsPerInchX;
        _position = new OverlayPosition(pointer.X - PlacementMath.ToPixels(start.OffsetX, dpi),
            pointer.Y - PlacementMath.ToPixels(start.OffsetY, dpi)) { MonitorId = DragWorkArea?.MonitorId };
        DesktopWindow.Move(start.Handle, _position.X, _position.Y);
        if (crossed)
            WorkAreaChanged?.Invoke();
    }

    private void Complete()
    {
        if (_start is not { } start)
            return;
        Move();
        Stop();
        if (_position is { } position && (position.X != start.Left || position.Y != start.Top))
            Completed?.Invoke(position);
        DragWorkArea = null;
        WorkAreaChanged?.Invoke();
    }

    private void Stop()
    {
        _start = null;
        _sample.Stop();
        if (_window.IsMouseCaptured)
            _window.ReleaseMouseCapture();
    }

    private sealed record DragStart(nint Handle, int Left, int Top, double OffsetX, double OffsetY);
}
