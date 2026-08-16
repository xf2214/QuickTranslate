using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Translation;

/// <summary>
/// 翻译提供方分发器：按 AppSettings.TranslationProvider 把请求路由到
/// 自定义 OpenAI 兼容大模型或 Qwen-MT（DashScope）。设置保存后立即生效，
/// 无需重启（AppSettings 单例被 SettingsManager 就地更新）。
/// </summary>
public class TranslationProviderDispatcher : ITranslationProvider
{
    private readonly CustomOpenAiTranslationProvider _custom;
    private readonly QwenMtTranslationProvider _qwenMt;
    private readonly IOptions<AppSettings> _settings;
    private readonly ILogger<TranslationProviderDispatcher> _logger;

    public TranslationProviderDispatcher(
        CustomOpenAiTranslationProvider custom,
        QwenMtTranslationProvider qwenMt,
        IOptions<AppSettings> settings,
        ILogger<TranslationProviderDispatcher> logger)
    {
        _custom = custom;
        _qwenMt = qwenMt;
        _settings = settings;
        _logger = logger;
    }

    public string Name => ActiveProvider.Name;

    private ITranslationProvider ActiveProvider =>
        _settings.Value.TranslationProvider == TranslationProviderKind.QwenMt ? _qwenMt : _custom;

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var provider = ActiveProvider;
        _logger.LogDebug("Translation routed to {Provider} (kind={Kind})",
            provider.Name, _settings.Value.TranslationProvider);

        await foreach (var chunk in provider.TranslateAsync(request, ct))
        {
            yield return chunk;
        }
    }
}
