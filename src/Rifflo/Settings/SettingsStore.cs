using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rifflo.Settings;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.GetFullPath(path);

    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rifflo", "settings.json");

    internal static string CurrentUserPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("RIFFLO_SETTINGS_PATH");
            return string.IsNullOrWhiteSpace(overridePath) ? DefaultPath : Path.GetFullPath(overridePath);
        }
    }

    public static SettingsStore ForCurrentUser() => new(CurrentUserPath);

    public AppSettings Load()
    {
        try
        {
            using var file = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AppSettings>(file, Json)?.Normalize() ?? AppSettings.Defaults;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppSettings.Defaults;
        }
    }

    public bool Save(AppSettings settings)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, settings.Normalize(), Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}

// Consume the whole value before converting so an incompatible new field cannot discard older settings.
internal abstract class CompatibleSettingConverter<T>(T fallback) : JsonConverter<T>
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = JsonElement.ParseValue(ref reader);
        try { return value.Deserialize<T>(options); }
        catch (JsonException) { return fallback; }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

internal sealed class LayoutSettingConverter() : CompatibleSettingConverter<OverlayLayout>(AppSettings.Defaults.Layout);
internal sealed class TextWidthSettingConverter() : CompatibleSettingConverter<double>(AppSettings.Defaults.TextWidth);
internal sealed class ScrollModeSettingConverter() : CompatibleSettingConverter<CaptionScrollMode>(AppSettings.Defaults.ScrollMode);
internal sealed class MonitorIdSettingConverter() : CompatibleSettingConverter<string?>(null);
internal sealed class PositionLockedSettingConverter() : CompatibleSettingConverter<bool>(AppSettings.Defaults.PositionLocked);
internal sealed class LiveCaptureEnabledSettingConverter() : CompatibleSettingConverter<bool>(AppSettings.Defaults.LiveCaptureEnabled);
internal sealed class BroadcastCaptureEnabledSettingConverter() : CompatibleSettingConverter<bool>(AppSettings.Defaults.BroadcastCaptureEnabled);

internal sealed class AnimationFrameRateSettingConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var frameRate))
            return frameRate;

        reader.Skip();
        return AppSettings.DefaultAnimationFrameRate;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
