using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Cache;

public class DefaultTranslationRouter : ITranslationRouter
{
    private readonly ITranslationCache _cache;
    private readonly ITranslationL2Cache? _l2Cache;
    private readonly ILocalDictionary _dictionary;
    private readonly ITranslationProvider _provider;
    private readonly IOptions<AppSettings> _settings;
    private readonly ILogger<DefaultTranslationRouter> _logger;

    public DefaultTranslationRouter(
        ITranslationCache cache,
        ILocalDictionary dictionary,
        ITranslationProvider provider,
        IOptions<AppSettings> settings,
        ILogger<DefaultTranslationRouter> logger,
        ITranslationL2Cache? l2Cache = null)
    {
        _cache = cache;
        _dictionary = dictionary;
        _provider = provider;
        _settings = settings;
        _logger = logger;
        _l2Cache = l2Cache;
    }

    public async Task<TranslationResult> TranslateWordAsync(string word, string targetLang, CancellationToken ct = default)
    {
        var normalizedKey = NormalizeKey(word, targetLang);

        if (_cache.TryGet(normalizedKey, out var cached))
        {
            _logger.LogDebug("L1 cache hit for {Key}", normalizedKey);
            return cached with { FromCache = true };
        }

        if (_l2Cache != null)
        {
            var l2Result = await _l2Cache.TryGetAsync(normalizedKey, ct);
            if (l2Result != null)
            {
                _logger.LogDebug("L2 cache hit for {Key}", normalizedKey);
                _cache.Add(normalizedKey, l2Result);
                return l2Result with { FromCache = true };
            }
        }

        if (_dictionary.TryLookup(word, targetLang, out var dictResult))
        {
            _logger.LogDebug("Dictionary hit for {Word}", word);
            _cache.Add(normalizedKey, dictResult);
            if (_l2Cache != null)
            {
                await _l2Cache.AddAsync(normalizedKey, dictResult, ct);
            }
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
        if (_l2Cache != null)
        {
            await _l2Cache.AddAsync(normalizedKey, result, ct);
        }
        return result;
    }

    public async Task<TranslationResult> TranslateBlockAsync(string blockText, string targetLang, CancellationToken ct = default)
    {
        var normalizedKey = NormalizeKey(blockText, targetLang);

        if (_cache.TryGet(normalizedKey, out var cached))
        {
            _logger.LogDebug("L1 cache hit for block {Key}", normalizedKey);
            return cached with { FromCache = true };
        }

        if (_l2Cache != null)
        {
            var l2Result = await _l2Cache.TryGetAsync(normalizedKey, ct);
            if (l2Result != null)
            {
                _logger.LogDebug("L2 cache hit for block {Key}", normalizedKey);
                _cache.Add(normalizedKey, l2Result);
                return l2Result with { FromCache = true };
            }
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
        if (_l2Cache != null)
        {
            await _l2Cache.AddAsync(normalizedKey, result, ct);
        }
        return result;
    }

    private async Task<string> ConsumeFullTranslationAsync(TranslationRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? finalText = null;
        await foreach (var chunk in _provider.TranslateAsync(request, ct))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                sb.Append(chunk.TextDelta);
            }

            if (chunk.IsFinal && !string.IsNullOrWhiteSpace(chunk.FullTranslation))
            {
                finalText = chunk.FullTranslation;
                break;
            }
        }

        var full = finalText ?? sb.ToString();
        // 空/纯空白在线结果视为响应异常：若不拦截，空译文会写入 L1/L2 缓存，
        // 导致同一 key 后续永远命中空结果、弹窗长期空白。
        if (string.IsNullOrWhiteSpace(full))
        {
            throw TranslationException.InvalidResponse("翻译服务返回内容为空");
        }

        return full;
    }

    private static string NormalizeKey(string text, string lang)
    {
        return $"{(text ?? string.Empty).Trim().ToLowerInvariant()}||{(lang ?? string.Empty).Trim().ToLowerInvariant()}";
    }
}
