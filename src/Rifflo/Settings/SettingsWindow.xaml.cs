using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Rifflo.Overlay;
using Forms = System.Windows.Forms;

namespace Rifflo.Settings;

internal partial class SettingsWindow : Window
{
    private AppSettings _settings = AppSettings.Defaults;
    private string[] _sources = [];
    private bool _synchronizing = true;
    private bool _closingForExit;

    public SettingsWindow()
    {
        InitializeComponent();
        var targetArea = DesktopWindow.GetCursorPos(out var cursor) ? DesktopWindow.FindWorkArea(cursor) : null;
        if ((targetArea ?? DesktopWindow.FindPrimaryWorkArea()) is { } workArea)
        {
            var scale = 96d / workArea.Dpi;
            var availableWidth = (workArea.Right - workArea.Left) * scale;
            var availableHeight = (workArea.Bottom - workArea.Top) * scale;
            MinWidth = Math.Min(MinWidth, availableWidth);
            MinHeight = Math.Min(MinHeight, availableHeight);
            Width = Math.Min(Width, availableWidth);
            Height = Math.Min(Height, availableHeight);
        }
        _synchronizing = false;
    }

    public event Action<AppSettings>? OptionsUpdated;
    public event Action? ResetPosition;
    public event Action? ResetAll;

    public void SetSettings(AppSettings settings)
    {
        var rebuildSources = PreferredSource.Items.Count == 0 || _settings.PreferredSource != settings.PreferredSource;
        _synchronizing = true;
        _settings = settings;
        LayoutOption.SelectedValue = settings.Layout;
        TextWidthSlider.Value = settings.TextWidth;
        ScrollModeOption.SelectedValue = settings.ScrollMode;
        Animation60Fps.IsChecked = settings.AnimationFrameRate == AppSettings.HighAnimationFrameRate;
        PositionLockedOption.IsChecked = settings.PositionLocked;
        ShowArtist.IsChecked = settings.ShowArtist;
        FontSizeSlider.Value = settings.FontSize;
        OpacitySlider.Value = settings.Opacity * 100;
        BarCountSlider.Value = settings.BarCount;
        SpectrumEnabled.IsChecked = settings.SpectrumEnabled;
        LiveCaptureEnabled.IsChecked = settings.LiveCaptureEnabled;
        BroadcastCaptureOption.IsChecked = settings.BroadcastCaptureEnabled;
        SensitivitySlider.Value = settings.Sensitivity;
        TopmostOption.IsChecked = settings.Topmost;
        SetColorButton(TextColorButton, settings.TextColor);
        SetColorButton(BarColorButton, settings.BarColor);
        if (rebuildSources)
            PopulateSources();
        UpdateLabels(settings);
        _synchronizing = false;
    }

    public void SetSources(IReadOnlyList<string> sources)
    {
        if (_sources.SequenceEqual(sources, StringComparer.OrdinalIgnoreCase))
            return;
        _synchronizing = true;
        _sources = sources.ToArray();
        PopulateSources();
        _synchronizing = false;
    }

    public void SetStatus(string text) => PlaybackStatus.Text = text;
    public void SetSaveStatus(bool saved) => SaveStatus.Text = saved ? "" : "设置未保存";

    public void CloseForExit()
    {
        _closingForExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (!_closingForExit)
        {
            args.Cancel = true;
            Hide();
        }
        base.OnClosing(args);
    }

    private void PopulateSources()
    {
        var options = new List<SourceOption> { new("", "自动选择") };
        options.AddRange(_sources.Select(source => new SourceOption(source, FormatSource(source))));
        var preferred = options.FirstOrDefault(option => string.Equals(option.Id, _settings.PreferredSource, StringComparison.OrdinalIgnoreCase));
        if (preferred is null)
        {
            preferred = new SourceOption(_settings.PreferredSource, FormatSource(_settings.PreferredSource) + "（未运行）");
            options.Add(preferred);
        }
        PreferredSource.ItemsSource = options;
        PreferredSource.SelectedItem = preferred;
    }

    public static string FormatSource(string source)
    {
        var appName = source.Split('!', 2)[0];
        if (appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetFileNameWithoutExtension(appName);
        var suffix = appName.LastIndexOf('_');
        return suffix > 0 && source.Contains('!') ? appName[..suffix] : appName;
    }

    private void OptionsChanged(object sender, RoutedEventArgs args)
    {
        if (_synchronizing)
            return;
        OptionsUpdated?.Invoke(_settings with
        {
            Layout = LayoutOption.SelectedValue is OverlayLayout layout ? layout : _settings.Layout,
            TextWidth = TextWidthSlider.Value,
            ScrollMode = ScrollModeOption.SelectedValue is CaptionScrollMode scrollMode ? scrollMode : _settings.ScrollMode,
            PositionLocked = PositionLockedOption.IsChecked == true,
            ShowArtist = ShowArtist.IsChecked == true,
            FontSize = FontSizeSlider.Value,
            Opacity = OpacitySlider.Value / 100,
            BarCount = (int)BarCountSlider.Value,
            SpectrumEnabled = SpectrumEnabled.IsChecked == true,
            LiveCaptureEnabled = LiveCaptureEnabled.IsChecked == true,
            Sensitivity = SensitivitySlider.Value,
            Topmost = TopmostOption.IsChecked == true,
            PreferredSource = PreferredSource.SelectedValue as string ?? ""
        });
    }

    private void AnimationFrameRateChanged(object sender, RoutedEventArgs args)
    {
        if (!_synchronizing)
        {
            var frameRate = Animation60Fps.IsChecked == true ? AppSettings.HighAnimationFrameRate : AppSettings.DefaultAnimationFrameRate;
            OptionsUpdated?.Invoke(_settings with { AnimationFrameRate = frameRate });
        }
    }

    private void BroadcastCaptureChanged(object sender, RoutedEventArgs args)
    {
        if (!_synchronizing)
            OptionsUpdated?.Invoke(_settings with { BroadcastCaptureEnabled = BroadcastCaptureOption.IsChecked == true });
    }

    private void UpdateLabels(AppSettings settings)
    {
        TextWidthValue.Text = $"{settings.TextWidth:0.##} DIP";
        FontSizeValue.Text = $"{settings.FontSize:0} DIP";
        OpacityValue.Text = $"{settings.Opacity:P0}";
        BarCountValue.Text = $"{settings.BarCount}";
        SensitivityValue.Text = $"{settings.Sensitivity:0.00}×";
        SpectrumOptions.IsEnabled = settings.SpectrumEnabled;
    }

    private void ChooseTextColor(object sender, RoutedEventArgs args) => ChooseColor(_settings.TextColor, color => _settings with { TextColor = color });
    private void ChooseBarColor(object sender, RoutedEventArgs args) => ChooseColor(_settings.BarColor, color => _settings with { BarColor = color });

    private void ChooseColor(string color, Func<string, AppSettings> changed)
    {
        var parsed = (Color)ColorConverter.ConvertFromString(color);
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(parsed.R, parsed.G, parsed.B)
        };
        if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK)
            OptionsUpdated?.Invoke(changed($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"));
    }

    private static void SetColorButton(Button button, string color)
    {
        if (button.Tag is string previous && previous == color)
            return;
        button.Tag = color;
        var swatch = new Border
        {
            Width = 16,
            Height = 16,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.SlateGray,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(swatch);
        content.Children.Add(new TextBlock { Text = color });
        button.Content = content;
    }

    private void DefaultPosition_Click(object sender, RoutedEventArgs args) => ResetPosition?.Invoke();
    private void ResetDefaults_Click(object sender, RoutedEventArgs args) => ResetAll?.Invoke();

    private sealed record SourceOption(string Id, string Name);
    private sealed record DialogOwner(nint Handle) : Forms.IWin32Window;
}
