using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Abstractions;

public interface ILocalDictionary
{
    bool TryLookup(string word, string targetLang, out TranslationResult result);

    /// <summary>Exact vocabulary membership; implementations can avoid creating a translation result.</summary>
    bool ContainsWord(string word) => TryLookup(word, "zh", out _);

    int Count { get; }
}
