using QuickTranslate.Core.Options;

namespace QuickTranslate.Core.Abstractions;

public interface ISettingsManager
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    void SetApiKey(string? plainKey);
    string? GetApiKey();
}
