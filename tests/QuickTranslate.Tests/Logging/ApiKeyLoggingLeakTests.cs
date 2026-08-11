using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Translation;
using QuickTranslate.Tests.Infrastructure;
using Xunit;

namespace QuickTranslate.Tests.Logging;

public class ApiKeyLoggingLeakTests
{
    const string SecretApiKey = "sk-qwen-12345-abcde-SECRET-98765";

    [Fact]
    public void TranslationException_AuthFailed_DoesNotLeakApiKey()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger("Test", entries);

        var authEx = TranslationException.AuthFailed("Auth failed: invalid key");
        logger.LogError(authEx, "Translation pipeline error");

        Assert.All(entries, e =>
        {
            Assert.DoesNotContain(SecretApiKey, e.Message ?? "", StringComparison.OrdinalIgnoreCase);
            foreach (var kv in e.State)
            {
                var val = kv.Value?.ToString() ?? "";
                Assert.DoesNotContain(SecretApiKey, val, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public void QwenMtOptions_Logged_DoesNotLeakApiKey()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger("Test", entries);

        var opts = new QwenMtOptions { ApiKey = SecretApiKey, MockMode = true };
        var options = Options.Create(opts);

        logger.LogInformation("Qwen provider configured. MockMode={MockMode}, BaseAddress={BaseAddress}",
            options.Value.MockMode, options.Value.BaseAddress);

        logger.LogInformation("Provider options: {@Options}", new
        {
            options.Value.MockMode,
            options.Value.UseStreaming,
            BaseAddress = options.Value.BaseAddress,
            HasApiKey = !string.IsNullOrWhiteSpace(options.Value.ApiKey)
        });

        Assert.All(entries, e =>
        {
            Assert.DoesNotContain(SecretApiKey, e.Message ?? "", StringComparison.OrdinalIgnoreCase);
            foreach (var kv in e.State)
            {
                var val = kv.Value?.ToString() ?? "";
                Assert.DoesNotContain(SecretApiKey, val, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Bearer", val, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public void AppSettings_IsApiKeyConfigured_DoesNotLeakKey()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger("Test", entries);

        var settings = new AppSettings
        {
            ResolvedApiKey = SecretApiKey,
            TargetLanguage = "zh-CN",
            DebugLogging = true
        };

        logger.LogInformation(
            "Settings loaded. IsApiKeyConfigured={IsApiKeyConfigured}, TargetLang={TargetLang}, Debug={Debug}",
            settings.IsApiKeyConfigured,
            settings.TargetLanguage,
            settings.DebugLogging);

        Assert.All(entries, e =>
        {
            Assert.DoesNotContain(SecretApiKey, e.Message ?? "", StringComparison.OrdinalIgnoreCase);
            foreach (var kv in e.State)
            {
                var val = kv.Value?.ToString() ?? "";
                Assert.DoesNotContain(SecretApiKey, val, StringComparison.OrdinalIgnoreCase);
            }
        });
    }
}
