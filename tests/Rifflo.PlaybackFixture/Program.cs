using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Devices;
using MediaPlayer = Windows.Media.Playback.MediaPlayer;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--sessions"])
        {
            return DumpSessionsAsync().GetAwaiter().GetResult();
        }

        if (args is ["--audio-sessions"])
        {
            Console.WriteLine(JsonSerializer.Serialize(AudioSessionProbe.Read(), Json));
            return 0;
        }

        if (args is ["--mute-fixture-session", var fixturePid, var mute])
        {
            using var fixture = Process.GetProcessById(int.Parse(fixturePid));
            if (fixture.ProcessName != "Rifflo.PlaybackFixture" || fixture.SessionId != Process.GetCurrentProcess().SessionId)
                return 2;

            Console.WriteLine(JsonSerializer.Serialize(AudioSessionProbe.SetMute((uint)fixture.Id, bool.Parse(mute)), Json));
            return 0;
        }

        if (args is ["--integrity", var targetPid])
        {
            Console.WriteLine(JsonSerializer.Serialize(ProcessIntegrity.Read(int.Parse(targetPid)), Json));
            return 0;
        }

        if (args is ["--media", var source, ..])
        {
            return ControlMediaAsync(source, args.Skip(2).ToArray()).GetAwaiter().GetResult();
        }

        if (args is ["--render-endpoints"])
        {
            return DumpRenderEndpointsAsync().GetAwaiter().GetResult();
        }

        if (args is ["--window-aumid", var processId])
        {
            using var process = Process.GetProcessById(int.Parse(processId));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                pid = process.Id,
                window = process.MainWindowHandle.ToInt64(),
                aumid = WindowIdentity.Get(process.MainWindowHandle),
            }, Json));
            return 0;
        }

        if (args is ["--watch-visibility", var windowProcessId, var outputPath])
            return WindowVisibilityProbe.Watch(int.Parse(windowProcessId), outputPath);

        if (args is ["--surface", var left, var top, var width, var height, var shade, var hitLog])
        {
            var surfaceApp = new Application();
            var button = new Button
            {
                Background = new SolidColorBrush(shade == "light" ? Color.FromRgb(236, 239, 243) : Color.FromRgb(35, 42, 50)),
            };
            var surface = new Window
            {
                Title = "Rifflo verification click surface",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Content = button,
            };
            surface.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(surface).Handle;
                SetWindowPos(handle, 0, int.Parse(left), int.Parse(top), int.Parse(width), int.Parse(height), 0x0040);
            };
            button.Click += (_, _) => File.AppendAllText(hitLog, DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine);
            return surfaceApp.Run(surface);
        }

        if (args is ["--generate", var directory])
        {
            Directory.CreateDirectory(directory);
            WriteTone(Path.Combine(directory, "low.wav"), 200, false);
            WriteTone(Path.Combine(directory, "high.wav"), 6000, false);
            WriteTone(Path.Combine(directory, "intermittent-high.wav"), 6000, true);
            WriteTone(Path.Combine(directory, "long-silence-high.wav"), 6000, 24, silenceStartSeconds: 4, silenceEndSeconds: 16);
            return 0;
        }

        if (args is ["--generate-animation", var animationDirectory])
        {
            Directory.CreateDirectory(animationDirectory);
            WriteTone(Path.Combine(animationDirectory, "steady.wav"), 200, 45);
            WriteWave(Path.Combine(animationDirectory, "dynamic.wav"), 120, time =>
            {
                // Frequencies and envelopes all close at the loop boundary.
                static double Wave(double seconds, double frequency) => Math.Sin(2 * Math.PI * frequency * seconds);
                return (1450 + 1200 * Wave(time, 1.375)) * Wave(time, 180)
                     + (950 + 760 * Wave(time, 2.125)) * Wave(time, 850)
                     + (850 + 710 * Wave(time, 1.75)) * Wave(time, 3100)
                     + (600 + 500 * Wave(time, 2.625)) * Wave(time, 6400);
            });
            return 0;
        }

        if (args is ["--generate-track-switch", var switchDirectory])
        {
            Directory.CreateDirectory(switchDirectory);
            WriteTone(Path.Combine(switchDirectory, "switch-low.wav"), 200, 45);
            WriteTone(Path.Combine(switchDirectory, "switch-high.wav"), 6000, 45);
            WriteTone(Path.Combine(switchDirectory, "switch-silence.wav"), 6000, 45, toneSeconds: 0);
            return 0;
        }

        if (args is ["--generate-kugou", var toneDirectory])
        {
            Directory.CreateDirectory(toneDirectory);
            WriteTone(Path.Combine(toneDirectory, "Rifflo 200 Hz.wav"), 200, 300);
            WriteTone(Path.Combine(toneDirectory, "Rifflo 6000 Hz.wav"), 6000, 300);
            WriteTone(Path.Combine(toneDirectory, "Rifflo 200 Hz followed by silence.wav"), 200, 300, toneSeconds: 15);
            WriteTone(Path.Combine(toneDirectory, "edge-kugou-high.wav"), 6000, 4);
            return 0;
        }

        if (args is not [var controlPath, var aumid, var label])
        {
            Console.Error.WriteLine("Usage: <control-json> <AUMID> <window-label> | --generate <media-directory> | --sessions");
            return 2;
        }

        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(aumid));
        var app = new Application();
        var status = new TextBlock { Margin = new Thickness(16), Text = "Waiting for playback command" };
        var window = new Window
        {
            Title = label,
            Width = 330,
            Height = 105,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowActivated = false,
            Content = status,
        };
        WindowIdentity.Set(new WindowInteropHelper(window).EnsureHandle(), aumid);

        using var player = new MediaPlayer { IsLoopingEnabled = true, Volume = 0.35 };
        var transport = player.SystemMediaTransportControls;
        player.CommandManager.IsEnabled = false;
        transport.IsPlayEnabled = true;
        transport.IsPauseEnabled = true;
        transport.IsEnabled = true;
        transport.PlaybackStatus = MediaPlaybackStatus.Stopped;

        string? openedFile = null;
        var audioDisconnected = false;
        string? lastToken = null;
        string? mediaError = null;
        bool applying = false;
        var reportPath = Path.ChangeExtension(controlPath, ".status.json");
        player.MediaFailed += (_, error) => Volatile.Write(ref mediaError, error.ErrorMessage);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
        timer.Tick += async (_, _) =>
        {
            if (applying)
                return;

            applying = true;
            try
            {
                if (!File.Exists(controlPath))
                {
                    return;
                }

                var command = JsonSerializer.Deserialize<PlaybackCommand>(File.ReadAllText(controlPath), Json);
                if (command is null || command.Token == lastToken)
                {
                    return;
                }

                lastToken = command.Token;
                if (command.State == "exit")
                {
                    window.Close();
                    return;
                }

                if (command.Volume is double volume)
                    player.Volume = Math.Clamp(volume, 0, 1);
                if (command.AudioDeviceId is { Length: > 0 } audioDeviceId && player.AudioDevice?.Id != audioDeviceId)
                {
                    if (command.ReopenOnRoute)
                    {
                        player.Pause();
                        player.Source = null;
                        openedFile = null;
                    }
                    player.AudioDevice = await DeviceInformation.CreateFromIdAsync(audioDeviceId);
                }

                audioDisconnected = command.AudioDisconnected;
                if (audioDisconnected && openedFile is not null)
                {
                    player.Pause();
                    player.Source = null;
                    openedFile = null;
                }

                if (!audioDisconnected && command.File != openedFile)
                {
                    Volatile.Write(ref mediaError, null);
                    player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(command.File)));
                    openedFile = command.File;
                }

                var updater = transport.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = command.Title;
                updater.MusicProperties.Artist = command.Artist ?? label;
                updater.MusicProperties.AlbumTitle = command.AlbumTitle ?? "";
                updater.MusicProperties.TrackNumber = command.TrackNumber ?? 0;
                updater.Update();
                switch (command.State)
                {
                    case "playing":
                        if (!audioDisconnected)
                            player.Play();
                        transport.PlaybackStatus = MediaPlaybackStatus.Playing;
                        break;
                    case "paused":
                        player.Pause();
                        transport.PlaybackStatus = MediaPlaybackStatus.Paused;
                        break;
                    case "stopped":
                        player.Pause();
                        if (!audioDisconnected)
                            player.PlaybackSession.Position = TimeSpan.Zero;
                        transport.PlaybackStatus = MediaPlaybackStatus.Stopped;
                        break;
                    case "changing":
                        player.Pause();
                        transport.PlaybackStatus = MediaPlaybackStatus.Changing;
                        break;
                    default:
                        throw new InvalidDataException($"Invalid playback state {command.State}");
                }

                status.Text = $"{command.Title}  ·  {command.State}";
                WriteReport();
            }
            catch (IOException)
            {
                // A simultaneous atomic file replacement can race the open. Retry on the next tick.
            }
            catch (Exception error)
            {
                Volatile.Write(ref mediaError, error.ToString());
                WriteReport();
            }
            finally { applying = false; }
        };
        var reporting = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        reporting.Tick += (_, _) => WriteReport();
        window.Closed += (_, _) =>
        {
            timer.Stop();
            reporting.Stop();
            transport.PlaybackStatus = MediaPlaybackStatus.Closed;
            transport.IsEnabled = false;
        };
        timer.Start();
        reporting.Start();
        return app.Run(window);

        void WriteReport()
        {
            var sample = new
            {
                pid = Environment.ProcessId,
                requestedAumid = aumid,
                token = lastToken,
                audioDisconnected,
                transportState = transport.PlaybackStatus.ToString(),
                playbackState = player.PlaybackSession.PlaybackState.ToString(),
                positionMs = player.PlaybackSession.Position.TotalMilliseconds,
                stopwatchTimestamp = Stopwatch.GetTimestamp(),
                stopwatchFrequency = Stopwatch.Frequency,
                audioDeviceId = player.AudioDevice?.Id,
                audioDeviceName = player.AudioDevice?.Name,
                error = Volatile.Read(ref mediaError),
            };
            try
            {
                File.WriteAllText(reportPath + ".tmp", JsonSerializer.Serialize(sample, Json));
                File.Move(reportPath + ".tmp", reportPath, true);
            }
            catch (IOException)
            {
                // The status reader may briefly still hold the old file open.
            }
        }
    }

    private static async Task<int> DumpRenderEndpointsAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            defaultDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default),
            devices = devices.Select(device => new { device.Id, device.Name, device.IsEnabled }),
        }, Json));
        return 0;
    }

    private static async Task<int> DumpSessionsAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var observations = new List<object>();
        foreach (var session in manager.GetSessions())
        {
            var media = await session.TryGetMediaPropertiesAsync();
            observations.Add(new
            {
                session.SourceAppUserModelId,
                media.Title,
                media.Artist,
                status = session.GetPlaybackInfo().PlaybackStatus.ToString(),
            });
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            current = manager.GetCurrentSession()?.SourceAppUserModelId,
            sessions = observations,
        }, Json));
        return 0;
    }

    private static async Task<int> ControlMediaAsync(string source, string[] arguments)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var observations = new List<(GlobalSystemMediaTransportControlsSession Session,
            GlobalSystemMediaTransportControlsSessionMediaProperties Media)>();
        foreach (var session in manager.GetSessions().Where(session =>
                     string.Equals(session.SourceAppUserModelId, source, StringComparison.OrdinalIgnoreCase)))
        {
            var media = await session.TryGetMediaPropertiesAsync();
            if (arguments.Length < 2 || arguments[0] == "seek" || arguments[1] == media.Title)
                observations.Add((session, media));
        }

        if (observations.Count != 1)
        {
            Console.Error.WriteLine($"Expected a unique {source} media session, observed {observations.Count}.");
            return 3;
        }

        var (selected, properties) = observations[0];
        bool? acknowledged = arguments.FirstOrDefault() switch
        {
            null => null,
            "play" => await selected.TryPlayAsync(),
            "pause" => await selected.TryPauseAsync(),
            "next" => await selected.TrySkipNextAsync(),
            "previous" => await selected.TrySkipPreviousAsync(),
            "rewind-paused" => await RewindToStartPausedAsync(selected, properties.Title),
            "seek" when arguments.Length == 2 =>
                await selected.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(long.Parse(arguments[1])).Ticks),
            _ => throw new ArgumentException("Expected play, pause, next, previous, rewind-paused or seek <milliseconds>."),
        };
        var playback = selected.GetPlaybackInfo();
        var timeline = selected.GetTimelineProperties();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            selected.SourceAppUserModelId,
            properties.Title,
            properties.Artist,
            acknowledged,
            status = playback.PlaybackStatus.ToString(),
            positionMs = timeline.Position.TotalMilliseconds,
            durationMs = (timeline.EndTime - timeline.StartTime).TotalMilliseconds,
            timelineUtc = timeline.LastUpdatedTime,
            playback.PlaybackRate,
            playback.Controls.IsPlaybackPositionEnabled,
        }, Json));
        return acknowledged == false ? 4 : 0;
    }

    private static async Task<bool> RewindToStartPausedAsync(GlobalSystemMediaTransportControlsSession session, string title)
    {
        // Some players offer previous/next without publishing a writable GSMTC timeline.
        if (!await session.TrySkipNextAsync() || !await WaitForTrackAsync(expectedOriginal: false))
            return false;
        if (!await session.TrySkipPreviousAsync() || !await WaitForTrackAsync(expectedOriginal: true))
            return false;
        return await session.TryPauseAsync();

        async Task<bool> WaitForTrackAsync(bool expectedOriginal)
        {
            var start = Stopwatch.GetTimestamp();
            do
            {
                var media = await session.TryGetMediaPropertiesAsync();
                if (!string.IsNullOrEmpty(media.Title) && (media.Title == title) == expectedOriginal &&
                    session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return true;
                await Task.Delay(10);
            } while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(4));
            return false;
        }
    }

    private static void WriteTone(string file, int frequency, bool intermittent) =>
        WriteTone(file, frequency, 8, intermittent ? 4 : 8);

    private static void WriteTone(string file, int frequency, int seconds, int? toneSeconds = null,
        int? silenceStartSeconds = null, int? silenceEndSeconds = null)
    {
        var audibleUntil = toneSeconds ?? seconds;
        var silenceStart = silenceStartSeconds ?? seconds;
        var silenceEnd = silenceEndSeconds ?? seconds;
        WriteWave(file, seconds, time => time < audibleUntil && (time < silenceStart || time >= silenceEnd)
            ? 1900 * Math.Sin(2 * Math.PI * frequency * time)
            : 0);
    }

    private static void WriteWave(string file, int seconds, Func<double, double> sample)
    {
        const int sampleRate = 48000;
        int byteCount = sampleRate * seconds * sizeof(short);
        using var stream = File.Create(file);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(byteCount + 36);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(byteCount);

        for (var i = 0; i < sampleRate * seconds; i++)
            writer.Write((short)sample((double)i / sampleRate));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    private sealed record PlaybackCommand(string Token, string File, string Title, string State,
        string? AudioDeviceId = null, double? Volume = null, bool ReopenOnRoute = false, string? Artist = null,
        string? AlbumTitle = null, uint? TrackNumber = null, bool AudioDisconnected = false);
}
