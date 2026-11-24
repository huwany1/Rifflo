using System.IO;
using System.Text.Json;

namespace Rifflo;

internal sealed class DiagnosticWriter(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _temporary = path + "." + Environment.ProcessId + ".tmp";

    public void Write<T>(T state)
    {
        try
        {
            File.WriteAllText(_temporary, JsonSerializer.Serialize(state, Json));
            File.Move(_temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Diagnostic readers may still hold the previous snapshot open. Retry at the next sample.
        }
    }

    public void Stop()
    {
        try { File.Delete(_temporary); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static DiagnosticWriter? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("RIFFLO_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            return new DiagnosticWriter(fullPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
