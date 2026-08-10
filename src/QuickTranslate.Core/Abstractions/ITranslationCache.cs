using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Abstractions;

public interface ITranslationCache
{
    int Capacity { get; }

    int Count { get; }

    bool TryGet(string normalizedKey, out TranslationResult value);

    void Add(string normalizedKey, TranslationResult value);

    void Remove(string normalizedKey);

    void Clear();
}
