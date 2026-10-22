using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslate.Core.Options;
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

    /// <summary>
    /// Versioned identity for router results. Legacy entries lack mode/provider settings and
    /// cannot safely be reused. Keep case-sensitive source/model data inside a digest so
    /// keys are unambiguous and do not expose OCR text or credentials.
    /// </summary>
    public static string BuildForRequest(string text, string targetLang, TranslationMode mode,
        AppSettings settings, string providerName)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Text = (text ?? string.Empty).Trim().Normalize(NormalizationForm.FormC),
            Provider = providerName,
            Kind = settings.TranslationProvider,
            Quality = settings.TranslationQuality,
            BaseUrl = (settings.CustomLlmBaseUrl ?? string.Empty).Trim(),
            Model = (settings.CustomLlmModel ?? string.Empty).Trim(),
            // Demo results must not survive configuring the real service. Never include the key itself.
            HasCredentials = settings.IsApiKeyConfigured
        });
        return Build("auto", (targetLang ?? string.Empty).Trim().ToLowerInvariant(), mode,
            "v2:" + Sha256Hex(payload));
    }

    public static string Sha256Hex(string payload)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload ?? ""));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
