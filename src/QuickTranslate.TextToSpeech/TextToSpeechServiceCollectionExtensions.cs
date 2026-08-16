using Microsoft.Extensions.DependencyInjection;

namespace QuickTranslate.TextToSpeech;

public static class TextToSpeechServiceCollectionExtensions
{
    public static IServiceCollection AddTextToSpeech(this IServiceCollection services)
    {
        services.AddSingleton<ITextToSpeechService, WindowsSapiTextToSpeechService>();
        return services;
    }
}