using System.Runtime.CompilerServices;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Services;

public class StubTranslationProvider : ITranslationProvider
{
    public string Name => "Stub";

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var targetText = "在线翻译接入 M5";
        var chunkSize = Math.Max(1, targetText.Length / 3);
        int produced = 0;

        while (produced < targetText.Length)
        {
            ct.ThrowIfCancellationRequested();
            int take = Math.Min(chunkSize, targetText.Length - produced);
            var delta = targetText.Substring(produced, take);
            produced += take;
            await Task.Delay(50, ct);
            yield return new TranslationChunk(TextDelta: delta, IsFinal: false);
        }

        yield return new TranslationChunk(TextDelta: "", IsFinal: true, FullTranslation: targetText);
    }
}
