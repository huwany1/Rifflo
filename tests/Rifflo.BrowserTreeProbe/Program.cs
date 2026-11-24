using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

const int SampleRate = 44100;
using var target = Process.GetProcessById(int.Parse(args[0]));
if (!string.Equals(target.ProcessName, "msedge", StringComparison.OrdinalIgnoreCase))
    return 2;

var path = args[1];
var probeOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var output = new StreamWriter(path) { AutoFlush = true };
await using var recorder = await new WasapiRecorderBuilder()
    .WithProcessLoopback((uint)target.Id, ProcessLoopbackMode.IncludeTargetProcessTree)
    .WithFormat(new WaveFormat(SampleRate, 16, 2)).WithEventSync().WithBufferLength(30).BuildAsync();
var gate = new object();
var packets = 0;
double lowAmplitude = 0;
double highAmplitude = 0;
recorder.DataAvailable += OnData;
recorder.StartRecording();
try
{
    for (var index = 0; index < 300 && !target.HasExited; index++)
    {
        await Task.Delay(250);
        lock (gate)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                unixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rootPid = target.Id, packets,
                lowAmplitude, highAmplitude
            }, probeOptions));
            packets = 0;
            lowAmplitude = highAmplitude = 0;
        }
    }
}
finally
{
    recorder.DataAvailable -= OnData;
    output.Dispose();
}
return 0;

void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
{
    var stereo = MemoryMarshal.Cast<byte, short>(buffer);
    var audible = (flags & AudioClientBufferFlags.Silent) == 0;
    var low = audible ? Amplitude(stereo, 200) : 0;
    var high = audible ? Amplitude(stereo, 6000) : 0;
    lock (gate)
    {
        packets++;
        lowAmplitude = Math.Max(lowAmplitude, low);
        highAmplitude = Math.Max(highAmplitude, high);
    }
}

static double Amplitude(ReadOnlySpan<short> stereo, double frequency)
{
    var frames = stereo.Length / 2;
    if (frames < 128)
        return 0;

    var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
    double prev = 0;
    double before = 0;
    for (var index = 0; index < frames; index++)
    {
        var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (frames - 1));
        var sample = (stereo[index * 2] + stereo[index * 2 + 1]) / 65536.0;
        var state = window * sample + coefficient * prev - before;
        before = prev;
        prev = state;
    }

    return 4 * Math.Sqrt(Math.Max(0, prev * prev + before * before - coefficient * prev * before)) / frames;
}
