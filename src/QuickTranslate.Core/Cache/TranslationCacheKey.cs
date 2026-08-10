using System.Security.Cryptography;
using System.Text;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Core.Cache;

public static class TranslationCacheKey
{
    public static string Normalize(string key) =>
        string.IsNullOrWhiteSpace(key) ? "" : key.Trim().Normalize(NormalizationForm.FormC);

    public static string Build(string srcLang, string dstLang, TranslationMode mode, string normalizedText)
        => Normalize($"{srcLang}|{dstLang}|{(int)mode}|{normalizedText}");

    public static string BuildLegacy(string text, string lang)
        => $"{(text ?? string.Empty).Trim().ToLowerInvariant()}||{(lang ?? string.Empty).Trim().ToLowerInvariant()}";

    public static string Sha256Hex(string payload)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload ?? ""));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
