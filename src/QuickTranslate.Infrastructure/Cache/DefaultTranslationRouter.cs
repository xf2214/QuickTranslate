using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Cache;

public class DefaultTranslationRouter : ITranslationRouter
{
    private readonly ITranslationCache _cache;
    private readonly ILocalDictionary _dictionary;
    private readonly ITranslationProvider _provider;
    private readonly ILogger<DefaultTranslationRouter> _logger;

    public DefaultTranslationRouter(
        ITranslationCache cache,
        ILocalDictionary dictionary,
        ITranslationProvider provider,
        ILogger<DefaultTranslationRouter> logger)
    {
        _cache = cache;
        _dictionary = dictionary;
        _provider = provider;
        _logger = logger;
    }

    public async Task<TranslationResult> TranslateWordAsync(string word, string targetLang, CancellationToken ct = default)
    {
        var normalizedKey = NormalizeKey(word, targetLang);

        if (_cache.TryGet(normalizedKey, out var cached))
        {
            _logger.LogDebug("Cache hit for {Key}", normalizedKey);
            return cached with { FromCache = true };
        }

        if (_dictionary.TryLookup(word, targetLang, out var dictResult))
        {
            _logger.LogDebug("Dictionary hit for {Word}", word);
            _cache.Add(normalizedKey, dictResult);
            return dictResult with { NormalizedKey = normalizedKey };
        }

        _logger.LogDebug("Calling online provider for {Word}", word);
        var onlineResult = await _provider.TranslateWordAsync(word, targetLang, ct);
        var toCache = onlineResult with { NormalizedKey = normalizedKey };
        _cache.Add(normalizedKey, toCache);
        return toCache;
    }

    public async Task<TranslationResult> TranslateBlockAsync(string blockText, string targetLang, CancellationToken ct = default)
    {
        var normalizedKey = NormalizeKey(blockText, targetLang);

        if (_cache.TryGet(normalizedKey, out var cached))
        {
            _logger.LogDebug("Cache hit for block {Key}", normalizedKey);
            return cached with { FromCache = true };
        }

        _logger.LogDebug("Calling online provider for block");
        var onlineResult = await _provider.TranslateBlockAsync(blockText, targetLang, ct);
        var toCache = onlineResult with { NormalizedKey = normalizedKey };
        _cache.Add(normalizedKey, toCache);
        return toCache;
    }

    private static string NormalizeKey(string text, string lang)
    {
        return $"{(text ?? string.Empty).Trim().ToLowerInvariant()}||{(lang ?? string.Empty).Trim().ToLowerInvariant()}";
    }
}
