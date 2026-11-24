using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Rifflo.Media;

namespace Rifflo.Audio;

public sealed partial class AudioMonitor
{
    public AudioMonitor(bool collectDiagnostics = false)
        : this(static (sessions, topology) => new WindowsAudioSource(sessions, topology),
            WindowsAudioRecorder.OpenAsync, AudioTiming.Default, collectDiagnostics)
    {
    }
}

internal sealed class WindowsAudioSource(Action sessionChanged, Action topologyChanged) : IAudioSource
{
    private readonly WindowsAppRegistration registrations = new();
    private readonly KuGouExecutableTrust kuGouTrust = new();
    private AudioEndpointSessions? endpoints;
    private int deviceRevision = -1;

    public AudioObservation Inspect(string source, AudioCompetition competition, int revision, bool includeLevels, CancellationToken token)
    {
        endpoints ??= new AudioEndpointSessions(sessionChanged, topologyChanged);
        if (revision != deviceRevision)
        {
            endpoints.RefreshDevices();
            deviceRevision = revision;
        }

        token.ThrowIfCancellationRequested();
        if (!endpoints.HasDevices)
            throw new InvalidOperationException("No active audio output device.");

        var active = endpoints.ReadActive(includeLevels);
        token.ThrowIfCancellationRequested();
        var processes = ProcessSnapshot.Read(source, active, competition.Sources);
        token.ThrowIfCancellationRequested();
        var match = AudioSourceMatcher.Match(source, processes, active, LookupExecutableTargets, kuGouTrust.IsTrusted, competition.Sources);
        return new(match, includeLevels ? active : []);

        IReadOnlyList<string> LookupExecutableTargets(string id)
        {
            token.ThrowIfCancellationRequested();
            return registrations.GetExecutableTargets(id);
        }
    }

    public void Dispose() => endpoints?.Dispose();
}

internal sealed class WindowsAudioRecorder(SafeProcessHandle handle, long createdAt, WasapiRecorder recorder) : IAudioRecorder
{
    public event AudioPacket? Packet;
    public event Action? Stopped;

    public static async Task<IAudioRecorder?> OpenAsync(ProcessIdentity target, CancellationToken token)
    {
        var handle = ProcessSnapshot.OpenVerified(target);
        WasapiRecorder? recorder = null;
        bool transferred = false;
        try
        {
            if (handle.IsInvalid)
                return null;

            token.ThrowIfCancellationRequested();
            recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(target.Id, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(new WaveFormat(SpectrumAnalyzer.SampleRate, 16, 2))
                .WithBufferLength(30)
                .WithEventSync()
                .BuildAsync().ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            if (!ProcessSnapshot.IsCurrent(handle, target.CreatedAt))
                return null;

            transferred = true;
            return new WindowsAudioRecorder(handle, target.CreatedAt, recorder);
        }
        finally
        {
            if (!transferred)
            {
                handle.Dispose();
                if (recorder is not null)
                    await recorder.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public bool IsAlive => ProcessSnapshot.IsCurrent(handle, createdAt);

    public void Start()
    {
        recorder.DataAvailable += OnData;
        recorder.RecordingStopped += OnStopped;
        recorder.StartRecording();
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        // WASAPI reports the first frame's QPC in 100 ns units.
        var capturedAt = (flags & AudioClientBufferFlags.TimestampError) != 0 ? 0 :
            (long)(qpcPosition * ((double)Stopwatch.Frequency / TimeSpan.TicksPerSecond));
        Packet?.Invoke(buffer, (flags & AudioClientBufferFlags.Silent) != 0,
            (flags & AudioClientBufferFlags.DataDiscontinuity) != 0, capturedAt);
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
            Debug.WriteLine(args.Exception);
        Stopped?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        recorder.DataAvailable -= OnData;
        recorder.RecordingStopped -= OnStopped;
        handle.Dispose();
        await recorder.DisposeAsync().ConfigureAwait(false);
    }
}
