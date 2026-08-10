using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Cache;

public class DefaultTranslationRouter : ITranslationRouter
{
    private readonly ITranslationCache _cache;
    private readonly ILocalDictionary _dictionary;
    private readonly ITranslationProvider _provider;
    private readonly IOptions<AppSettings> _settings;
    private readonly ILogger<DefaultTranslationRouter> _logger;

    public DefaultTranslationRouter(
        ITranslationCache cache,
        ILocalDictionary dictionary,
        ITranslationProvider provider,
        IOptions<AppSettings> settings,
        ILogger<DefaultTranslationRouter> logger)
    {
        _cache = cache;
        _dictionary = dictionary;
        _provider = provider;
        _settings = settings;
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

        var request = new TranslationRequest(
            Text: word,
            Context: null,
            SourceLanguage: "auto",
            TargetLanguage: targetLang,
            Mode: TranslationMode.Word,
            OperationId: Guid.NewGuid(),
            Quality: _settings.Value.TranslationQuality);

        var fullTranslation = await ConsumeFullTranslationAsync(request, ct);
        var result = new TranslationResult(
            NormalizedKey: normalizedKey,
            SourceText: word,
            TargetText: fullTranslation,
            TargetLanguage: targetLang,
            FromCache: false,
            FromDictionary: false,
            NeedsOnline: true);

        _cache.Add(normalizedKey, result);
        return result;
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

        var request = new TranslationRequest(
            Text: blockText,
            Context: null,
            SourceLanguage: "auto",
            TargetLanguage: targetLang,
            Mode: TranslationMode.Block,
            OperationId: Guid.NewGuid(),
            Quality: _settings.Value.TranslationQuality);

        var fullTranslation = await ConsumeFullTranslationAsync(request, ct);
        var result = new TranslationResult(
            NormalizedKey: normalizedKey,
            SourceText: blockText,
            TargetText: fullTranslation,
            TargetLanguage: targetLang,
            FromCache: false,
            FromDictionary: false,
            NeedsOnline: true);

        _cache.Add(normalizedKey, result);
        return result;
    }

    private async Task<string> ConsumeFullTranslationAsync(TranslationRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in _provider.TranslateAsync(request, ct))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                sb.Append(chunk.TextDelta);
            }

            if (chunk.IsFinal && !string.IsNullOrEmpty(chunk.FullTranslation))
            {
                return chunk.FullTranslation;
            }
        }

        return sb.ToString();
    }

    private static string NormalizeKey(string text, string lang)
    {
        return $"{(text ?? string.Empty).Trim().ToLowerInvariant()}||{(lang ?? string.Empty).Trim().ToLowerInvariant()}";
    }
}
