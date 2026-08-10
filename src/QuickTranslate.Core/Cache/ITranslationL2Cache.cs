using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Cache;

public interface ITranslationL2Cache
{
    Task<TranslationResult?> TryGetAsync(string normalizedKey, CancellationToken ct = default);

    Task AddAsync(string normalizedKey, TranslationResult result, CancellationToken ct = default);

    Task<int> TrimAsync(int keep, CancellationToken ct = default);
}
