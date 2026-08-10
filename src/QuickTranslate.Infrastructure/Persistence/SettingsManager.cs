using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslate.Core.Options;

namespace QuickTranslate.Infrastructure.Persistence;

public class SettingsManager
{
    private readonly string _settingsFilePath;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolverChain = { JsonContext.Default }
    };

    public SettingsManager(string appDataDirectory)
    {
        _settingsFilePath = Path.Combine(appDataDirectory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            var defaultSettings = new AppSettings();
            await SaveAsync(defaultSettings, ct).ConfigureAwait(false);
            return defaultSettings;
        }

        var json = await File.ReadAllTextAsync(_settingsFilePath, ct).ConfigureAwait(false);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        if (settings == null)
        {
            settings = new AppSettings();
            await SaveAsync(settings, ct).ConfigureAwait(false);
        }
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        json = SanitizeSettingsJson(json);
        await File.WriteAllTextAsync(_settingsFilePath, json, ct).ConfigureAwait(false);
    }

    private static string SanitizeSettingsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ApiKey", out _))
            {
                var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (root != null && root.Remove("ApiKey"))
                {
                    json = JsonSerializer.Serialize(root, SerializerOptions);
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        return json;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(HotkeyCombo))]
[JsonSerializable(typeof(TranslationQuality))]
[JsonSerializable(typeof(HotkeyModifiers))]
[JsonSerializable(typeof(KeyboardKey))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal partial class JsonContext : JsonSerializerContext
{
}
