using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Abstractions;

public interface ILocalDictionary
{
    bool TryLookup(string word, string targetLang, out TranslationResult result);

    int Count { get; }
}
