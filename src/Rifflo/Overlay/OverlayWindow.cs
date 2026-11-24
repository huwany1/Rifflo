using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rifflo.Settings;

namespace Rifflo.Overlay;

internal sealed class OverlayWindow : Window
{
    private static readonly Brush DragBackground = FrozenBrush(Color.FromArgb(6, 0, 0, 0));
    private readonly Border _surface;
    private readonly bool _preview;
    private readonly CaptionPanel _caption;
    private readonly SpectrumView _spectrum;
    private readonly OverlayLayoutPanel _layout;
    private readonly OverlayDragController _drag;
    private nint _handle;
    private OverlayPosition? _position;
    private AppSettings? _appearance;
    private bool _placementQueued;

    public OverlayWindow(bool preview, OverlayFrameClock frames, bool traceFrames = false)
    {
        _preview = preview;
        _caption = new CaptionPanel(frames, traceFrames);
        _spectrum = new SpectrumView(traceFrames);
        Title = preview ? "Rifflo 外观预览" : "Rifflo 桌面悬浮窗";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = DragBackground;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        _layout = new OverlayLayoutPanel(_caption, _spectrum);
        _surface = new Border
        {
            Padding = new Thickness(12, 7, 12, 8),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Child = _layout
        };
        Content = _surface;

        _drag = new OverlayDragController(this);
        _drag.WorkAreaChanged += QueuePlacement;
        _drag.Completed += FinishDrag;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_handle).AddHook(WindowMessage);
            ApplyWindowStyles();
        };
        SizeChanged += (_, _) => QueuePlacement();
        DpiChanged += (_, _) => QueuePlacement();
    }

    public event Action? PositionInvalidated;
    public event Action<OverlayPosition>? PositionDragged;
    public long DrawCount => _spectrum.DrawCount;
    public long SpectrumChangedCount => _spectrum.ChangedCount;
    public long CaptionOffsetCommitCount => _caption.OffsetCommitCount;
    public FrameSample[] GetSpectrumFrames() => _spectrum.GetFrames();
    public FrameSample[] GetCaptionFrames() => _caption.GetFrames();
    public double[] GetRenderedBarHeights() => _spectrum.IsVisible ? _spectrum.RenderedBarHeights.ToArray() : [];
    private bool PositionLocked => _appearance?.PositionLocked == true;
    private bool BroadcastCaptureEnabled => !_preview && _appearance?.BroadcastCaptureEnabled == true;

    public void SetTrack(string title, string artist, string source = "", long revision = 0) => _caption.SetTrack(title, artist, source, revision);
    public void SetBands(ReadOnlySpan<float> bands) => _spectrum.SetBands(bands);

    public void SetSpectrumVisible(bool visible) => _spectrum.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void ApplySettings(AppSettings settings)
    {
        var positionLockChanged = PositionLocked != settings.PositionLocked;
        var wasBroadcastCaptureEnabled = BroadcastCaptureEnabled;
        if (_appearance is null || _appearance.Topmost != settings.Topmost)
            Topmost = settings.Topmost;
        if (_appearance is null || _appearance.Opacity != settings.Opacity)
            Opacity = settings.Opacity;
        if (_appearance is null || _appearance.FontSize != settings.FontSize || _appearance.TextColor != settings.TextColor)
        {
            var foreground = (Color)ColorConverter.ConvertFromString(settings.TextColor);
            _caption.SetAppearance(settings.FontSize, FrozenBrush(foreground));
            if (_preview)
                UpdatePreviewSurface(foreground);
        }
        if (_appearance is null || _appearance.BarCount != settings.BarCount || _appearance.BarColor != settings.BarColor)
            _spectrum.SetAppearance(settings.BarCount, FrozenBrush((Color)ColorConverter.ConvertFromString(settings.BarColor)));

        _layout.SetLayout(settings.Layout);
        _caption.SetViewport(settings.TextWidth, settings.ScrollMode);

        _appearance = settings;
        if (positionLockChanged)
        {
            if (settings.PositionLocked)
                _drag.Cancel();
            _drag.Enabled = !settings.PositionLocked;
            Background = settings.PositionLocked ? Brushes.Transparent : DragBackground;
            QueuePlacement();
        }
        if (positionLockChanged || wasBroadcastCaptureEnabled != BroadcastCaptureEnabled)
            ApplyWindowStyles();
    }

    private void ApplyWindowStyles()
    {
        if (_handle != 0)
            DesktopWindow.Configure(_handle, PositionLocked, toolWindow: !BroadcastCaptureEnabled);
    }

    public void SetPosition(OverlayPosition? position)
    {
        if (_position != position)
        {
            _position = position;
            QueuePlacement();
        }
    }

    public void SetShown(bool show)
    {
        if (show == IsVisible)
            return;
        if (show)
        {
            ApplyWindowStyles();
            var savedArea = FindSavedArea();
            if ((savedArea ?? DesktopWindow.FindPrimaryWorkArea()) is { } area)
                LimitLayout(area, reserveDefaultMargin: savedArea is null);
            Show();
            UpdateLayout();
            Place();
        }
        else
        {
            _drag.Cancel();
            Hide();
        }
    }

    public bool TryGetBounds(out DesktopWindow.Rect bounds)
    {
        bounds = default;
        return _handle != 0 && DesktopWindow.GetWindowRect(_handle, out bounds);
    }

    internal OverlayLayoutSnapshot GetLayoutDiagnostics() => new(
        GetElementBounds(_caption), GetElementBounds(_spectrum), _caption.ContentWidth, _caption.Offset,
        _caption.IsScrolling, VisualTreeHelper.GetDpi(this).DpiScaleX);

    private OverlayElementBounds? GetElementBounds(FrameworkElement element)
    {
        if (!element.IsVisible)
            return null;

        var origin = element.TranslatePoint(new Point(0, 0), this);
        return new OverlayElementBounds(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            case DesktopWindow.MouseActivate:
                handled = true;
                return DesktopWindow.NoActivate;
            case DesktopWindow.HitTest when PositionLocked:
                handled = true;
                return DesktopWindow.HitTransparent;
            case DesktopWindow.DisplayChange:
            case DesktopWindow.SettingChange:
                QueuePlacement();
                break;
        }
        return 0;
    }

    private void QueuePlacement()
    {
        if (_placementQueued || Dispatcher.HasShutdownStarted)
            return;
        _placementQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _placementQueued = false;
            Place();
        }, DispatcherPriority.Loaded);
    }

    private WorkArea? FindSavedArea() => _position is { } position ? DesktopWindow.FindWorkArea(position) : null;

    private void Place()
    {
        if (!IsVisible || _handle == 0)
            return;

        if (_drag.IsDragging)
        {
            if (_drag.DragWorkArea is { } dragArea)
                LimitLayout(dragArea, reserveDefaultMargin: false);
            return;
        }

        if (DesktopWindow.FindPrimaryWorkArea() is not { } primary)
            return;

        var savedArea = FindSavedArea();
        LimitLayout(savedArea ?? primary, reserveDefaultMargin: savedArea is null);
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var placement = PlacementMath.Resolve(_position, savedArea, primary, ActualWidth, ActualHeight);
        MoveTo(placement.Position);
        if (placement.Recovered)
        {
            _position = null;
            PositionInvalidated?.Invoke();
        }
    }

    private void FinishDrag(OverlayPosition position)
    {
        if (DesktopWindow.FindPrimaryWorkArea() is not { } primary)
            return;

        var area = _drag.DragWorkArea ?? DesktopWindow.FindWorkArea(position);
        LimitLayout(area ?? primary, reserveDefaultMargin: area is null);
        var placement = PlacementMath.Resolve(position, area, primary, ActualWidth, ActualHeight);
        _position = placement.Position;
        MoveTo(_position);
        PositionDragged?.Invoke(_position);
    }

    private void LimitLayout(WorkArea area, bool reserveDefaultMargin)
    {
        var horizontalInsets = _surface.Padding.Left + _surface.Padding.Right +
            _surface.BorderThickness.Left + _surface.BorderThickness.Right;
        var maxWidth = OverlayLayoutPanel.WorkAreaContentWidth(area, horizontalInsets, reserveDefaultMargin);
        if (_layout.MaxWidth == maxWidth)
            return;

        _layout.MaxWidth = maxWidth;
        if (IsVisible)
            UpdateLayout();
    }

    private void MoveTo(OverlayPosition position)
    {
        if (!TryGetBounds(out var bounds) || bounds.Left != position.X || bounds.Top != position.Y)
            DesktopWindow.Move(_handle, position.X, position.Y);
    }

    private void UpdatePreviewSurface(Color color)
    {
        var luminance = color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722;
        _surface.Background = FrozenBrush(luminance >= 130
            ? Color.FromArgb(209, 17, 23, 34) : Color.FromArgb(228, 247, 250, 255));
        _surface.BorderBrush = FrozenBrush(luminance >= 130
            ? Color.FromArgb(126, 158, 194, 240) : Color.FromArgb(165, 49, 85, 130));
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

internal sealed record OverlayElementBounds(double X, double Y, double Width, double Height);

internal sealed record OverlayLayoutSnapshot(OverlayElementBounds? Caption, OverlayElementBounds? Spectrum,
    double CaptionContentWidth, double CaptionOffset, bool CaptionScrolling, double DpiScale);
