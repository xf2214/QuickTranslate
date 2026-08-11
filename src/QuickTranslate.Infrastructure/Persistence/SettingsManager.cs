using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;

namespace QuickTranslate.Infrastructure.Persistence;

public class SettingsManager : ISettingsManager
{
    private readonly string _settingsFilePath;
    private readonly ISecretStore _secretStore;
    private AppSettings? _loadedSettings;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolverChain = { JsonContext.Default }
    };

    public SettingsManager(string appDataDirectory, ISecretStore secretStore)
    {
        _settingsFilePath = Path.Combine(appDataDirectory, "settings.json");
        _secretStore = secretStore;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        AppSettings settings;
        if (!File.Exists(_settingsFilePath))
        {
            settings = new AppSettings();
            await SaveAsync(settings, ct).ConfigureAwait(false);
        }
        else
        {
            var json = await File.ReadAllTextAsync(_settingsFilePath, ct).ConfigureAwait(false);
            settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (settings == null)
            {
                settings = new AppSettings();
                await SaveAsync(settings, ct).ConfigureAwait(false);
            }
        }

        settings.ResolvedApiKey = _secretStore.Load("qwen.api.key");
        _loadedSettings = settings;
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
        _loadedSettings = settings;
    }

    public void SetApiKey(string? plainKey)
    {
        if (string.IsNullOrWhiteSpace(plainKey))
        {
            _secretStore.Delete("qwen.api.key");
            if (_loadedSettings != null)
                _loadedSettings.ResolvedApiKey = null;
        }
        else
        {
            var key = plainKey.Trim();
            if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                key = key.Substring("Bearer ".Length).Trim();
            _secretStore.Save("qwen.api.key", key);
            if (_loadedSettings != null)
                _loadedSettings.ResolvedApiKey = key;
        }
    }

    public string? GetApiKey()
    {
        if (_loadedSettings != null && !string.IsNullOrWhiteSpace(_loadedSettings.ResolvedApiKey))
            return _loadedSettings.ResolvedApiKey;
        var key = _secretStore.Load("qwen.api.key");
        if (_loadedSettings != null)
            _loadedSettings.ResolvedApiKey = key;
        return key;
    }

    private static string SanitizeSettingsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ApiKey", out _) ||
                doc.RootElement.TryGetProperty("ResolvedApiKey", out _) ||
                doc.RootElement.TryGetProperty("IsApiKeyConfigured", out _))
            {
                var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (root != null)
                {
                    root.Remove("ApiKey");
                    root.Remove("ResolvedApiKey");
                    root.Remove("IsApiKeyConfigured");
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

