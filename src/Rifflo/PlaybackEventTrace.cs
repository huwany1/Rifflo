using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace Rifflo;

internal sealed class PlaybackEventTrace
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<object> events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task writer;

    private PlaybackEventTrace(string path) => writer = Task.Run(() => WriteEventsAsync(path));

    public static PlaybackEventTrace? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("RIFFLO_EVENT_TRACE");
        return string.IsNullOrWhiteSpace(path) ? null : new(path);
    }

    public void Write<T>(T item) where T : class => events.Writer.TryWrite(item);

    public Task StopAsync()
    {
        events.Writer.TryComplete();
        return writer;
    }

    private async Task WriteEventsAsync(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using var stream = new StreamWriter(fullPath) { AutoFlush = true };
            await foreach (var item in events.Reader.ReadAllAsync().ConfigureAwait(false))
                await stream.WriteLineAsync(JsonSerializer.Serialize(item, Json)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            events.Writer.TryComplete();
            Debug.WriteLine(error);
        }
    }
}
