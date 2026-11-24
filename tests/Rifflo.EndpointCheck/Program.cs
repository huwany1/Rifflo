using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Rifflo.Audio;
using Rifflo.Media;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

if (args is not [var fixtureExe, var alternateName, var evidenceFile])
{
    Console.Error.WriteLine("Usage: <Rifflo.PlaybackFixture.exe> <alternate output name substring> <evidence.json>");
    return 2;
}

const string source = "Rifflo.Verification.Endpoint";
const string title = "Rifflo route test 200 Hz";
const double sensitivity = 3;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
fixtureExe = Path.GetFullPath(fixtureExe);
evidenceFile = Path.GetFullPath(evidenceFile);
var directory = Directory.CreateTempSubdirectory("Rifflo.EndpointCheck-");
var control = Path.Combine(directory.FullName, "player.json");
var tone = Path.Combine(directory.FullName, "low.wav");
var report = Path.ChangeExtension(control, ".status.json");
var phases = new List<Phase>();
var assembly = typeof(AudioMonitor).Assembly.Location;
var assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly)));
Process? fixture = null;
int? fixturePid = null;
string? defaultBefore = null;
string? defaultAfter = null;
string? error = null;
bool captureStopped = false;
try
{
    using (var generator = StartFixture(["--generate", directory.FullName]))
    {
        await generator.WaitForExitAsync();
        if (generator.ExitCode != 0 || !File.Exists(tone))
            throw new InvalidOperationException("The fixture could not create audio input.");
    }

    defaultBefore = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
    var endpoints = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
    var primary = endpoints.Single(endpoint => endpoint.Id == defaultBefore);
    var alternate = endpoints.Single(endpoint => endpoint.Id != primary.Id && endpoint.IsEnabled &&
        endpoint.Name.Contains(alternateName, StringComparison.OrdinalIgnoreCase));

    fixture = StartFixture([control, source, "Rifflo endpoint verification"]);
    fixturePid = fixture.Id;
    using var media = new MediaMonitor { PreferredSource = source };
    await using var audio = new AudioMonitor();
    TrackState selected = TrackState.Empty;
    media.Changed += track => Volatile.Write(ref selected, track);
    await media.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

    WriteCommand("playing", primary.Id);
    await audio.SelectAsync(source, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    phases.Add(await ObserveRoute("initial", primary, 0));
    WriteCommand("playing", alternate.Id);
    phases.Add(await ObserveRoute("alternate", alternate, audio.FftCount));
    WriteCommand("playing", primary.Id);
    phases.Add(await ObserveRoute("return", primary, audio.FftCount));

    defaultAfter = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
    if (defaultBefore != defaultAfter)
        throw new InvalidOperationException("The global rendering default changed during the check.");

    audio.Stop();
    await Task.Delay(250);
    var stoppedAt = audio.PacketCount;
    var stoppedFft = audio.FftCount;
    await Task.Delay(250);
    var cleared = new float[AudioMonitor.BandCount];
    audio.ReadBands(cleared, 1);
    captureStopped = !audio.Capturing && audio.PacketCount == stoppedAt && audio.FftCount == stoppedFft && cleared.All(level => level == 0);
    if (!captureStopped)
        throw new InvalidOperationException("Capture remained active after Stop.");

    async Task<Phase> ObserveRoute(string name, DeviceInformation endpoint, long previousFft)
    {
        var expectedEndpointId = endpoint.Id.Split('#')[2];
        var elapsed = Stopwatch.StartNew();
        var levels = new float[AudioMonitor.BandCount];
        string? playbackError = null;
        EndpointPeak[] observedEndpoints = [];
        float bestPeak = 0;
        int bestBand = 0;
        long? firstRoutedFft = null;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(12))
        {
            await Task.Delay(180);
            if (fixture.HasExited)
                throw new InvalidOperationException("The controlled playback process exited unexpectedly.");

            try
            {
                using var stream = File.OpenRead(report);
                var status = await JsonSerializer.DeserializeAsync<FixtureStatus>(stream, json);
                playbackError = status?.Error;
                if (status?.AudioDeviceId != endpoint.Id || status.PlaybackState != "Playing")
                    continue;
            }
            catch (IOException) { continue; }

            var track = Volatile.Read(ref selected);
            if (track.Source != source || track.Title != title || track.State != PlayState.Playing || !track.AudioEligible)
                continue;

            observedEndpoints = ReadEndpointPeaks(fixture.Id);
            audio.ReadBands(levels, sensitivity);
            float peak = levels.Max();
            int dominant = Array.IndexOf(levels, peak);
            if (peak > bestPeak)
            {
                bestPeak = peak;
                bestBand = dominant;
            }
            var audible = observedEndpoints.Where(observation => observation.Peak > 0.0001f)
                .Select(observation => observation.EndpointId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (audible.Length != 1 || !audible[0].Equals(expectedEndpointId, StringComparison.OrdinalIgnoreCase))
            {
                firstRoutedFft = null;
                continue;
            }

            firstRoutedFft ??= Math.Max(previousFft, audio.FftCount);
            if (!audio.Capturing || audio.FftCount <= firstRoutedFft + 5 || dominant > 6 || peak <= 0)
                continue;

            var result = new Phase(name, endpoint.Name, expectedEndpointId, track.Title,
                elapsed.ElapsedMilliseconds, audio.PacketCount, audio.FftCount, dominant, peak, observedEndpoints);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return result;
        }

        throw new InvalidOperationException($"{name}: {audio.Status}, {audio.PacketCount} packets, {audio.FftCount} FFT, " +
            $"expected endpoint {expectedEndpointId}, active endpoint peaks [{string.Join(", ", observedEndpoints.AsEnumerable())}], " +
            $"strongest displayed band {bestBand} ({bestPeak:F6}), fixture error {playbackError ?? "none"}.");
    }
}
catch (Exception exception)
{
    error = exception.ToString();
    Console.Error.WriteLine(error);
}
finally
{
    if (fixture is not null)
    {
        if (!fixture.HasExited)
        {
            WriteCommand("exit", null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await fixture.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { fixture.Kill(); await fixture.WaitForExitAsync(); }
        }
        fixture.Dispose();
    }

    Directory.CreateDirectory(Path.GetDirectoryName(evidenceFile)!);
    File.WriteAllText(evidenceFile, JsonSerializer.Serialize(new
    {
        utc = DateTimeOffset.UtcNow,
        source,
        fixtureExe,
        fixturePid,
        assembly,
        assemblySha256,
        sensitivity,
        defaultBefore,
        defaultAfter,
        phases,
        captureStopped,
        error
    }, json));
    foreach (var file in directory.EnumerateFiles())
        file.Delete();
    directory.Delete();
}

return error is null ? 0 : 1;

Process StartFixture(IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(fixtureExe) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("Fixture process could not start.");
}

void WriteCommand(string state, string? deviceId)
{
    var data = new { token = Guid.NewGuid().ToString("N"), file = tone, title, state, audioDeviceId = deviceId, volume = 0.045, reopenOnRoute = true };
    var pending = control + ".tmp";
    File.WriteAllText(pending, JsonSerializer.Serialize(data, json));
    File.Move(pending, control, overwrite: true);
}

static EndpointPeak[] ReadEndpointPeaks(int processId)
{
    using var enumerator = new MMDeviceEnumerator();
    using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
    var found = new List<EndpointPeak>();
    foreach (var current in devices)
    {
        using var device = current;
        var sessions = device.AudioSessionManager.Sessions;
        for (int index = 0, count = sessions.Count; index < count; index++)
        {
            using var session = sessions[index];
            if (session.GetProcessID == processId && session.State == AudioSessionState.AudioSessionStateActive)
            {
                found.Add(new EndpointPeak(device.ID, session.AudioMeterInformation.MasterPeakValue));
            }
        }
    }
    return found.ToArray();
}

internal sealed record FixtureStatus(string? AudioDeviceId, string PlaybackState, string? Error);
internal sealed record Phase(string Name, string DeviceName, string EndpointId, string Title,
    long RecoveryMs, long Packets, long Fft, int DominantBand, float Peak, EndpointPeak[] ActiveSessions);
internal sealed record EndpointPeak(string EndpointId, float Peak);
