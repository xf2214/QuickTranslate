using Microsoft.Extensions.DependencyInjection;
using QuickTranslate.TextToSpeech;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

/// <summary>
/// 朗读模块契约测试：验证 SAPI 实现在无语音/异常环境下安全降级，
/// 以及 DI 注册可正常解析。
/// </summary>
public class TextToSpeechContractTests
{
    [Fact]
    public void Service_CanConstruct_WithoutThrowing()
    {
        using var service = new WindowsSapiTextToSpeechService();
        Assert.NotNull(service);
    }

    [Fact]
    public void IsSupported_ReturnsBoolean_WithoutThrowing()
    {
        using var service = new WindowsSapiTextToSpeechService();
        _ = service.IsSupported; // 不应抛异常
    }

    [Fact]
    public void Stop_NeverThrows()
    {
        using var service = new WindowsSapiTextToSpeechService();
        service.Stop();
    }

    [Fact]
    public void Speak_EmptyText_IsNoOp()
    {
        using var service = new WindowsSapiTextToSpeechService();
        service.Speak("   ");
        service.Speak(null!);
    }

    [Fact]
    public void Speak_WhenVoicesUnavailable_DoesNotThrow()
    {
        using var service = new WindowsSapiTextToSpeechService();
        if (service.IsSupported)
        {
            // 有语音环境：为不产生实际音频副作用，跳过真实朗读。
            return;
        }

        service.Speak("hello", "en");
    }

    [Fact]
    public void AddTextToSpeech_RegistersSingleton()
    {
        var provider = new ServiceCollection()
            .AddTextToSpeech()
            .BuildServiceProvider();

        var instance = provider.GetRequiredService<ITextToSpeechService>();
        Assert.NotNull(instance);

        var again = provider.GetRequiredService<ITextToSpeechService>();
        Assert.Same(instance, again);
    }
}