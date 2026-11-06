using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Cache;

public interface ITranslationL2Cache
{
    Task<TranslationResult?> TryGetAsync(string normalizedKey, CancellationToken ct = default);

    Task AddAsync(string normalizedKey, TranslationResult result, CancellationToken ct = default);

    Task<int> TrimAsync(int keep, CancellationToken ct = default);

    Task ClearAllAsync(CancellationToken ct = default) => Task.CompletedTask;

    Task<long> GetCountAsync(CancellationToken ct = default) => Task.FromResult(0L);
}
