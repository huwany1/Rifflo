using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Rifflo.Audio;
using Rifflo.Media;
using Rifflo.Overlay;
using Rifflo.Settings;

namespace Rifflo;

internal sealed class AppController : IDisposable
{
    private readonly App _application;
    private readonly SettingsStore _store = SettingsStore.ForCurrentUser();
    private readonly MediaMonitor _media;
    private readonly AudioMonitor _audio;
    private readonly OverlayFrameClock _frames = new();
    private readonly OverlayWindow _real;
    private readonly OverlayWindow _preview;
    private readonly Tray _tray;
    private readonly DispatcherTimer _pauseTail;
    private readonly DispatcherTimer _save;
    private readonly DispatcherTimer? _diagnosticTimer;
    private readonly DiagnosticWriter? _diagnostics = DiagnosticWriter.FromEnvironment();
    private readonly PlaybackEventTrace? _eventTrace = PlaybackEventTrace.FromEnvironment();
    private AppSettings _settings;
    private SettingsWindow? _settingsWindow;
    private TrackState _track = TrackState.Empty;
    private TrackState _observedTrack = TrackState.Empty;
    private TrackState _appliedTrack = TrackState.Empty;
    private CancellationTokenSource? _selection;
    private string? _requestedSource;
    private long _requestedSessionId;
    private AudioCompetition _requestedCompetition = AudioCompetition.Empty;
    private long _requestedGeneration;
    private float[] _levels;
    private long _lastFrame;
    private long _animationStartedAt;
    private long? _pauseStartedAt;
    private bool _manuallyHidden;
    private bool _tailHasSpectrum;
    private bool _spectrumVisible;
    private bool _spectrumFramesRunning;
    private SpectrumPresentation _spectrum;
    private bool _needsSave;
    private bool _saveSucceeded = true;
    private bool _mediaFailed;
    private bool _audioFailed;
    private bool _disposed;

    public AppController(App application, string trayName, string? testMediaPrefix)
    {
        _application = application;
        _media = new MediaMonitor { TestSourcePrefix = testMediaPrefix };
        _audio = new AudioMonitor(collectDiagnostics: _diagnostics is not null || _eventTrace is not null);
        _settings = _store.Load();
        _levels = new float[_settings.BarCount];
        _frames.SetFrameRate(_settings.AnimationFrameRate);
        var traceFrames = _diagnostics is not null && Environment.GetEnvironmentVariable("RIFFLO_FRAME_TRACE") == "1";
        if (_diagnostics is not null)
            _frames.EnableDiagnostics(Environment.GetEnvironmentVariable("RIFFLO_CLOCK_TRACE") == "1");
        _real = new OverlayWindow(preview: false, _frames, traceFrames);
        _preview = new OverlayWindow(preview: true, _frames, traceFrames);
        _pauseTail = MakeTimer((int)OverlayPlayback.PauseDuration.TotalMilliseconds, CheckPauseTail);
        _save = MakeTimer(350, SaveSettings);
        _tray = new Tray(trayName, ShowSettings, TogglePositionLock, ToggleBroadcastCapture, ToggleVisibility, SetAnimationFrameRate, Quit);
        UpdateTray();

        _media.Changed += MediaChanged;
        _media.SourcesChanged += SourcesChanged;
        _audio.StatusChanged += AudioStatusChanged;
        _audio.BandsAvailable += BandsAvailable;
        if (_eventTrace is not null)
        {
            _audio.Trace += TraceAudio;
            _real.IsVisibleChanged += (_, _) => TraceVisibility("real");
            _preview.IsVisibleChanged += (_, _) => TraceVisibility("preview");
        }
        _real.PositionDragged += PositionDragged;
        _preview.PositionDragged += PositionDragged;
        _real.PositionInvalidated += ResetPosition;
        _preview.PositionInvalidated += ResetPosition;
        _media.PreferredSource = _settings.PreferredSource;
        ApplyAppearance();
        ApplyPosition();
        if (_diagnostics is not null)
            _diagnosticTimer = MakeTimer(1000, WriteDiagnosticSnapshot);
    }

    public void Start()
    {
        _eventTrace?.Write(new { Kind = "AppStarted", StopwatchTimestamp = Stopwatch.GetTimestamp(),
            StopwatchFrequency = Stopwatch.Frequency, FrameRate = _settings.AnimationFrameRate });
        _diagnosticTimer?.Start();
        WriteDiagnosticSnapshot();
        _ = StartMediaAsync();
    }

    public void ShowSettings()
    {
        if (_disposed)
            return;

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.SetSettings(_settings);
            _settingsWindow.SetSources(_media.Sources);
            _settingsWindow.SetSaveStatus(_saveSucceeded);
            _settingsWindow.OptionsUpdated += UpdateSettings;
            _settingsWindow.ResetPosition += ResetPosition;
            _settingsWindow.ResetAll += ResetSettings;
            _settingsWindow.IsVisibleChanged += (_, _) =>
            {
                TraceVisibility("settings");
                Refresh();
            };
        }

        if (!_settingsWindow.IsVisible)
            _settingsWindow.Show();
        _settingsWindow.WindowState = WindowState.Normal;
        if (!_settingsWindow.Activate())
        {
            _settingsWindow.Topmost = true;
            _settingsWindow.Topmost = false;
        }
        UpdateStatus();
    }

    private async void Quit()
    {
        if (!StopInteraction())
            return;

        _real.SetShown(false);
        _preview.SetShown(false);
        _settingsWindow?.CloseForExit();
        var workersStopped = false;
        try
        {
            await Task.WhenAll(_audio.DisposeAsync().AsTask(), _media.DisposeAsync().AsTask())
                .WaitAsync(TimeSpan.FromSeconds(2));
            workersStopped = true;
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
        }
        finally
        {
            _eventTrace?.Write(new { Kind = "AppStopped", StopwatchTimestamp = Stopwatch.GetTimestamp(), WorkersStopped = workersStopped });
            if (_eventTrace is not null)
            {
                try { await _eventTrace.StopAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (TimeoutException error) { Debug.WriteLine(error); }
            }
            _application.Shutdown();
        }
    }

    public void Dispose()
    {
        if (StopInteraction())
        {
            _audio.Dispose();
            _media.Dispose();
            if (_eventTrace is not null)
                _ = _eventTrace.StopAsync();
        }
    }

    private bool StopInteraction()
    {
        if (_disposed)
            return false;
        _disposed = true;
        _eventTrace?.Write(new { Kind = "AppStopping", StopwatchTimestamp = Stopwatch.GetTimestamp() });
        SetSpectrumFramesRunning(false);
        _frames.Dispose();
        _pauseTail.Stop();
        _save.Stop();
        _diagnosticTimer?.Stop();
        _media.Changed -= MediaChanged;
        _media.SourcesChanged -= SourcesChanged;
        _audio.StatusChanged -= AudioStatusChanged;
        _audio.BandsAvailable -= BandsAvailable;
        _audio.CancelBandWait();
        StopAudio();
        _tray.Dispose();
        SaveSettings();
        _diagnostics?.Stop();
        return true;
    }

    private DispatcherTimer MakeTimer(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _application.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => action();
        return timer;
    }

    private async Task StartMediaAsync()
    {
        try
        {
            await _media.StartAsync();
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            if (!_disposed)
            {
                _mediaFailed = true;
                UpdateStatus();
            }
        }
    }

    private void FromWorker(Action action)
    {
        if (_application.Dispatcher.HasShutdownStarted)
            return;
        _application.Dispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
                action();
        }, DispatcherPriority.Normal);
    }

    private bool HasPendingTrack => !ReferenceEquals(_appliedTrack, Volatile.Read(ref _observedTrack));

    private void MediaChanged(TrackState state)
    {
        Volatile.Write(ref _observedTrack, state);
        TraceTrack("MediaObserved", state);
        _audio.ObserveMediaSelection(state);
        FromWorker(() =>
        {
            if (!ReferenceEquals(state, Volatile.Read(ref _observedTrack)))
                return;

            _appliedTrack = state;
            ApplyTrack(state);
            TraceTrack("MediaApplied", state);
        });
    }

    private void AudioStatusChanged() => FromWorker(() =>
    {
        // Let the new playback state handle revocation, including the fade from the last visible bars on pause.
        if (!HasPendingTrack)
            Refresh();
    });
    private void SourcesChanged() => FromWorker(() => _settingsWindow?.SetSources(_media.Sources));

    private void BandsAvailable() => FromWorker(() =>
    {
        if (!HasPendingTrack && _spectrum.Mode == SpectrumMode.Live && _real.IsVisible)
            SetSpectrumFramesRunning(true);
    });

    private void ApplyTrack(TrackState state)
    {
        var previous = _track;
        var sameSession = state.IsSameSession(previous);
        _track = state;
        if (state.State == PlayState.Paused && sameSession && state.HasTitle && _real.IsVisible)
        {
            if (previous.IsPlaying || previous.IsTransition || (_pauseStartedAt is { } startedAt && startedAt != state.ObservedAt))
            {
                _tailHasSpectrum = _spectrumVisible;
                _pauseStartedAt = state.ObservedAt;
                SchedulePauseTail(state.ObservedAt);
            }
        }
        else
        {
            EndPauseTail();
        }

        if (!state.IsTransition && (!sameSession || !previous.AudioCompetition.Equals(state.AudioCompetition)) && !_spectrum.UsesPreset)
            ClearSpectrum();
        ApplyCaption();
        Refresh();
    }

    private void CheckPauseTail()
    {
        if (!HasPendingTrack && _pauseStartedAt is { } pausedAt && !SchedulePauseTail(pausedAt))
            Refresh();
    }

    private bool SchedulePauseTail(long pausedAt)
    {
        var remaining = OverlayPlayback.PauseRemaining(pausedAt, Stopwatch.GetTimestamp());
        if (remaining <= TimeSpan.Zero)
        {
            EndPauseTail();
            return false;
        }

        _pauseTail.Stop();
        _pauseTail.Interval = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
        _pauseTail.Start();
        return true;
    }

    private void EndPauseTail()
    {
        _pauseTail.Stop();
        _pauseStartedAt = null;
        _tailHasSpectrum = false;
    }

    private void Refresh()
    {
        if (_disposed)
            return;

        var now = Stopwatch.GetTimestamp();
        if (_pauseStartedAt is { } pausedAt && OverlayPlayback.PauseRemaining(pausedAt, now) <= TimeSpan.Zero)
            EndPauseTail();
        var playing = _track.IsPlaying;
        var (showReal, showPreview) = OverlayPlayback.Resolve(_track, _pauseStartedAt.HasValue, _manuallyHidden, _settingsWindow?.IsVisible == true);
        if (!_track.IsTransition || !MatchesRequestedTrack || !SpectrumPresentation.CaptureEnabled(_settings, _track, _manuallyHidden))
            ReconcileAudio(SpectrumPresentation.WantsCapture(_settings, _track, playing, _manuallyHidden));

        if (!_settings.SpectrumEnabled)
            _tailHasSpectrum = false;
        _spectrumVisible = showReal && _settings.SpectrumEnabled && (playing || _track.IsTransition || _tailHasSpectrum);
        var previous = _spectrum;
        _spectrum = SpectrumPresentation.Resolve(_settings, _track, playing, _manuallyHidden,
            showPauseSpectrum: !playing && _spectrumVisible, showPreview, _audio.CaptureStatus, _requestedGeneration, _audioFailed);
        var modeChanged = previous.Mode != _spectrum.Mode;
        if (modeChanged && _spectrum.Mode != SpectrumMode.Live)
            _audio.CancelBandWait();
        if ((_spectrum.UsesPreset && !previous.UsesPreset) || (_spectrum.Mode == SpectrumMode.Preview && modeChanged))
            _animationStartedAt = now;
        if (_spectrum.Mode is SpectrumMode.None or SpectrumMode.Hidden or SpectrumMode.Disabled or SpectrumMode.Connecting or SpectrumMode.Preview)
            ClearSpectrum();

        _real.SetSpectrumVisible(_spectrumVisible);
        _preview.SetSpectrumVisible(_settings.SpectrumEnabled);
        if (_spectrum.DrawsFrames(_settings.SpectrumEnabled, Array.Exists(_levels, static level => level > 0)))
        {
            if (modeChanged || !_spectrumFramesRunning)
                _lastFrame = now;
            SetSpectrumFramesRunning(true);
            if (modeChanged && _spectrum.UsesPreset)
                DrawFrame(now);
            else if (modeChanged && _spectrum.Mode == SpectrumMode.Live)
                DrawLiveBands(now);
        }
        else
        {
            SetSpectrumFramesRunning(false);
        }

        if (showReal)
        {
            _preview.SetShown(false);
            _real.SetShown(true);
        }
        else
        {
            _real.SetShown(false);
            _preview.SetShown(showPreview);
        }

        UpdateTray();
        UpdateStatus();
    }

    private void SetSpectrumFramesRunning(bool running)
    {
        if (_spectrumFramesRunning == running)
            return;

        _spectrumFramesRunning = running;
        if (running)
        {
            _audio.CancelBandWait();
            _frames.Tick += DrawFrame;
        }
        else
        {
            _frames.Tick -= DrawFrame;
        }
    }

    private void ReconcileAudio(bool needed)
    {
        if (!needed)
        {
            StopAudio();
            return;
        }
        if (MatchesRequestedTrack &&
            (_audioFailed || _audio.CaptureStatus.State != AudioCaptureState.Idle))
            return;

        StopAudio();
        _audioFailed = false;
        _requestedSource = _track.Source;
        _requestedSessionId = _track.SessionId;
        _requestedCompetition = _track.AudioCompetition;
        _selection = new CancellationTokenSource();
        _ = SelectAudioAsync(_requestedSource, _requestedCompetition, _selection.Token, _requestedSessionId);
        _requestedGeneration = _audio.CaptureStatus.Generation;
    }

    private bool MatchesRequestedTrack => _requestedSource is { } source && _requestedSessionId == _track.SessionId &&
        MediaSelection.SourceEquals(source, _track.Source) && _requestedCompetition.Equals(_track.AudioCompetition);

    private async Task SelectAudioAsync(string source, AudioCompetition competition, CancellationToken cancellation, long sessionId)
    {
        try
        {
            await _audio.SelectAsync(source, competition, cancellation, sessionId);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            if (!_disposed && !cancellation.IsCancellationRequested)
            {
                _audioFailed = true;
                Refresh();
            }
        }
    }

    private void StopAudio()
    {
        if (_selection is null)
            return;

        _requestedSource = null;
        _requestedSessionId = 0;
        _requestedCompetition = AudioCompetition.Empty;
        _requestedGeneration = 0;
        var selection = _selection;
        _selection = null;
        selection.Cancel();
        _audio.Stop();
        selection.Dispose();
    }

    private void DrawFrame(long now)
    {
        var elapsed = Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;
        switch (_spectrum.Mode)
        {
            case SpectrumMode.Live:
                var capture = _audio.CaptureStatus;
                if (capture.State != AudioCaptureState.Live || capture.Generation != _requestedGeneration)
                {
                    if (!HasPendingTrack)
                        Refresh();
                    return;
                }
                if (!DrawLiveBands(now))
                {
                    SetSpectrumFramesRunning(false);
                    _audio.WaitForBands(_settings.Sensitivity);
                }
                return;
            case SpectrumMode.ManualPreset:
            case SpectrumMode.AutomaticPreset:
                SpectrumAnimation.FillPreset(_levels, Stopwatch.GetElapsedTime(_animationStartedAt, now).TotalSeconds, _settings.Sensitivity);
                break;
            case SpectrumMode.Preview when _settings.SpectrumEnabled:
                Span<float> sample = stackalloc float[_settings.BarCount];
                SpectrumAnimation.FillPreset(sample, Stopwatch.GetElapsedTime(_animationStartedAt, now).TotalSeconds, _settings.Sensitivity);
                _preview.SetBands(sample);
                return;
            case SpectrumMode.Pausing:
            case SpectrumMode.Muted:
            case SpectrumMode.Transitioning:
                if (!SpectrumAnimation.FadeToSilence(_levels, elapsed))
                    SetSpectrumFramesRunning(false);
                break;
            default:
                return;
        }

        _real.SetBands(_levels);
    }

    private bool DrawLiveBands(long now)
    {
        var moving = _audio.ReadBands(_levels, _settings.Sensitivity, now, out var sampleGeneration);
        _real.SetBands(_levels);
        _audio.BandsSubmitted(sampleGeneration);
        return moving;
    }

    private void ClearSpectrum()
    {
        Array.Clear(_levels);
        _real.SetBands(_levels);
    }

    private void ToggleVisibility()
    {
        _manuallyHidden = !_manuallyHidden;
        _eventTrace?.Write(new { Kind = "VisibilityToggled", StopwatchTimestamp = Stopwatch.GetTimestamp(), ManuallyHidden = _manuallyHidden });
        if (_manuallyHidden)
            EndPauseTail();
        Refresh();
    }

    private void TogglePositionLock() => UpdateSettings(_settings with { PositionLocked = !_settings.PositionLocked });

    private void ToggleBroadcastCapture() => UpdateSettings(_settings with { BroadcastCaptureEnabled = !_settings.BroadcastCaptureEnabled });

    private void SetAnimationFrameRate(int frameRate) => UpdateSettings(_settings with { AnimationFrameRate = frameRate });

    private void UpdateTray() => _tray.Update(_settings.PositionLocked, _settings.BroadcastCaptureEnabled, _manuallyHidden,
        _settings.AnimationFrameRate);

    private void PositionDragged(OverlayPosition position)
    {
        UpdateSettings(_settings with { Position = position });
        SaveSettings();
    }

    private void ResetPosition() => UpdateSettings(_settings with { Position = null });

    private void ResetSettings() => UpdateSettings(AppSettings.Defaults);

    private void ApplyPosition()
    {
        _real.SetPosition(_settings.Position);
        _preview.SetPosition(_settings.Position);
    }

    private void UpdateSettings(AppSettings settings)
    {
        settings = settings.Normalize();
        if (_settings == settings)
            return;

        var oldSettings = _settings;
        _settings = settings;
        _eventTrace?.Write(new { Kind = "SettingsUpdated", StopwatchTimestamp = Stopwatch.GetTimestamp(), settings.AnimationFrameRate,
            settings.LiveCaptureEnabled, settings.SpectrumEnabled, settings.BroadcastCaptureEnabled });
        if (settings.AnimationFrameRate != oldSettings.AnimationFrameRate)
            _frames.SetFrameRate(settings.AnimationFrameRate);

        var otherSettingsChanged = settings != (oldSettings with
        {
            AnimationFrameRate = settings.AnimationFrameRate,
            BroadcastCaptureEnabled = settings.BroadcastCaptureEnabled
        });
        if (otherSettingsChanged)
        {
            if (settings.BarCount != oldSettings.BarCount)
                _levels = new float[settings.BarCount];
            if (settings.PreferredSource != oldSettings.PreferredSource)
                _media.PreferredSource = settings.PreferredSource;
            if (settings.Position != oldSettings.Position)
                ApplyPosition();
            ApplyAppearance();
        }
        else if (settings.BroadcastCaptureEnabled != oldSettings.BroadcastCaptureEnabled)
        {
            _real.ApplySettings(settings);
        }

        _settingsWindow?.SetSettings(_settings);
        _needsSave = true;
        _save.Stop();
        _save.Start();
        if (otherSettingsChanged)
            Refresh();
        else
            UpdateTray();
    }

    private void ApplyAppearance()
    {
        _real.ApplySettings(_settings);
        _preview.ApplySettings(_settings);
        ApplyCaption();
        _preview.SetTrack("That Girl&Talking to the Moon (Full)", _settings.ShowArtist ? "4AGX" : "");
    }

    private void ApplyCaption() => _real.SetTrack(OneLine(_track.Title), _settings.ShowArtist ? OneLine(_track.Artist) : "",
        _track.Source, _track.CaptionRevision);

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ')
        .Replace('\v', ' ').Replace('\f', ' ').Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ').Trim();

    private void UpdateStatus()
    {
        if (_settingsWindow?.IsVisible == true)
            _settingsWindow.SetStatus(GetStatusText());
    }

    private string GetStatusText()
    {
        var player = SettingsWindow.FormatSource(_track.Source);
        var playback = _track.IsTransition ? "正在切换歌曲" : _track.IsPlaying ? "正在播放" : "已暂停";
        var source = _track.HasTitle
            ? player.Length == 0 ? playback : $"{playback} · {player}"
            : _mediaFailed ? "暂时无法读取系统媒体会话" : "等待支持系统媒体信息的播放器";
        var spectrum = _spectrum.Mode switch
        {
            SpectrumMode.Hidden => "悬浮窗已隐藏",
            SpectrumMode.Disabled => "频谱显示已关闭",
            SpectrumMode.Connecting => "正在连接播放器音频…",
            SpectrumMode.Live => "正在显示实时频谱",
            SpectrumMode.Muted => "播放器已静音",
            SpectrumMode.ManualPreset => "实时采集已关闭，正在播放预制动画",
            SpectrumMode.AutomaticPreset when _spectrum.Reason is AudioCaptureReason.AmbiguousSource or AudioCaptureReason.UntrustedIdentity =>
                "暂时无法确定哪个音频属于当前播放器，正在播放预制动画。恢复后会自动切回。",
            SpectrumMode.AutomaticPreset => "暂时无法读取当前播放器的音频，已切换为预制动画。恢复后会自动切回。",
            SpectrumMode.Preview when !_settings.SpectrumEnabled => "外观预览 · 频谱显示已关闭",
            SpectrumMode.Preview => "无播放 · 外观预览",
            SpectrumMode.Pausing => "频谱已暂停",
            _ when _manuallyHidden && _track.HasTitle => "悬浮窗已隐藏",
            _ when !_settings.SpectrumEnabled => "频谱显示已关闭",
            _ when _track.HasTitle && _track.State == PlayState.Paused => "频谱已暂停",
            _ => ""
        };
        return spectrum.Length == 0 ? source : $"{source}\n{spectrum}";
    }

    private void SaveSettings()
    {
        _save.Stop();
        if (!_needsSave)
            return;

        _saveSucceeded = _store.Save(_settings);
        _needsSave = !_saveSucceeded;
        _settingsWindow?.SetSaveStatus(_saveSucceeded);
    }

    private void TraceTrack(string kind, TrackState state) => _eventTrace?.Write(new
    {
        Kind = kind,
        StopwatchTimestamp = Stopwatch.GetTimestamp(),
        state.ObservedAt,
        state.Source,
        state.SessionId,
        State = state.State.ToString(),
        state.IsTransition,
        state.Title,
        state.Artist,
        state.CaptionRevision,
        state.AudioEligible,
        CompetingAudioSources = state.AudioCompetition.Sources
    });

    private void TraceVisibility(string window) => _eventTrace?.Write(new
    {
        Kind = "WindowVisibility",
        StopwatchTimestamp = Stopwatch.GetTimestamp(),
        Window = window,
        Real = _real.IsVisible,
        Preview = _preview.IsVisible,
        SettingsVisible = _settingsWindow?.IsVisible == true,
        _track.Title,
        _track.Source,
        State = _track.State.ToString(),
        _track.IsTransition,
        FrameRate = _settings.AnimationFrameRate
    });

    private void TraceAudio(AudioCaptureEvent entry) => _eventTrace?.Write(new
    {
        entry.Kind,
        StopwatchTimestamp = entry.Timestamp,
        entry.Source,
        entry.RequestGeneration,
        entry.SampleGeneration,
        entry.ConnectionId,
        entry.NativeOpenCount,
        entry.ProcessId,
        entry.ProcessCreatedAt,
        entry.EndpointIds,
        entry.Silent,
        entry.PeakBand
    });

    private void WriteDiagnosticSnapshot()
    {
        if (_diagnostics is null)
            return;

        var foregroundOverlay = _preview.IsVisible ? _preview : _real;
        foregroundOverlay.TryGetBounds(out var bounds);
        var capture = _audio.CaptureStatus;
        var now = Stopwatch.GetTimestamp();
        double? presetAnimationSeconds = _spectrum.UsesPreset || (_spectrum.Mode == SpectrumMode.Preview && _settings.SpectrumEnabled)
            ? Stopwatch.GetElapsedTime(_animationStartedAt, now).TotalSeconds
            : null;
        _diagnostics.Write(new
        {
            UnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            StopwatchTimestamp = now,
            StopwatchFrequency = Stopwatch.Frequency,
            RenderingTier = System.Windows.Media.RenderCapability.Tier >> 16,
            Environment.ProcessId,
            _track.Title,
            _track.Artist,
            _track.Source,
            _track.CaptionRevision,
            _track.SessionId,
            _track.IsTransition,
            _track.AudioEligible,
            CompetingAudioSources = _track.AudioCompetition.Sources,
            State = _track.State.ToString(),
            Visible = _real.IsVisible,
            Preview = _preview.IsVisible,
            OverlayWindowHandle = new System.Windows.Interop.WindowInteropHelper(_real).Handle.ToInt64(),
            PreviewWindowHandle = new System.Windows.Interop.WindowInteropHelper(_preview).Handle.ToInt64(),
            SettingsVisible = _settingsWindow?.IsVisible == true,
            _settings.PositionLocked,
            _settings.SpectrumEnabled,
            _settings.LiveCaptureEnabled,
            _settings.BroadcastCaptureEnabled,
            _settings.AnimationFrameRate,
            ManuallyHidden = _manuallyHidden,
            _audio.Capturing,
            AudioStatus = _audio.Status,
            AudioMatch = _audio.ReadDiagnostics(),
            CaptureState = capture.State.ToString(),
            CaptureReason = capture.Reason.ToString(),
            CaptureRunning = capture.Recording,
            CaptureSource = capture.Source,
            CaptureGeneration = capture.Generation,
            _audio.MatchAttemptCount,
            _audio.CaptureAttemptCount,
            _audio.PacketCount,
            _audio.FftCount,
            _real.DrawCount,
            PreviewDrawCount = _preview.DrawCount,
            FrameTimerRunning = _spectrumFramesRunning,
            AnimationClockRunning = _frames.IsRunning,
            _frames.AnimationTickCount,
            AnimationTicks = _frames.GetFrames(),
            _real.SpectrumChangedCount,
            _real.CaptionOffsetCommitCount,
            PreviewSpectrumChangedCount = _preview.SpectrumChangedCount,
            PreviewCaptionOffsetCommitCount = _preview.CaptionOffsetCommitCount,
            SpectrumFrames = _real.GetSpectrumFrames(),
            CaptionFrames = _real.GetCaptionFrames(),
            PreviewSpectrumFrames = _preview.GetSpectrumFrames(),
            PreviewCaptionFrames = _preview.GetCaptionFrames(),
            SpectrumVisible = _spectrumVisible,
            PreviewSpectrumVisible = _preview.IsVisible && _settings.SpectrumEnabled,
            SpectrumMode = _spectrum.Mode.ToString(),
            SpectrumReason = _spectrum.Reason.ToString(),
            PresetAnimationSeconds = presetAnimationSeconds,
            StatusText = GetStatusText(),
            RenderedBarHeights = foregroundOverlay.GetRenderedBarHeights(),
            Bands = _levels,
            X = bounds.Left,
            Y = bounds.Top,
            bounds.Width,
            bounds.Height,
            OverlayLayout = foregroundOverlay.GetLayoutDiagnostics()
        });
    }
}
