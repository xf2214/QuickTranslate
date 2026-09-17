using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class DisableSystemProxySettingsTests
{
    [Fact]
    public void AppSettings_Default_DisableSystemProxy_IsTrue()
    {
        var s = new AppSettings();
        Assert.True(s.DisableSystemProxy);
    }

    [Fact]
    public void AppSettings_ApplyFrom_Copies_DisableSystemProxy_True()
    {
        var source = new AppSettings { DisableSystemProxy = true };
        var target = new AppSettings { DisableSystemProxy = false };
        target.ApplyFrom(source);
        Assert.True(target.DisableSystemProxy);
    }

    [Fact]
    public void AppSettings_ApplyFrom_Copies_DisableSystemProxy_False()
    {
        var source = new AppSettings { DisableSystemProxy = false };
        var target = new AppSettings { DisableSystemProxy = true };
        target.ApplyFrom(source);
        Assert.False(target.DisableSystemProxy);
    }

    [Fact]
    public void AppSettings_JsonRoundtrip_DisableSystemProxy_True_Survives()
    {
        var s = new AppSettings { DisableSystemProxy = true };
        var json = JsonSerializer.Serialize(s);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.True(restored!.DisableSystemProxy);
        Assert.Contains("DisableSystemProxy", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSettings_JsonRoundtrip_DisableSystemProxy_False_Survives()
    {
        var s = new AppSettings { DisableSystemProxy = false };
        var json = JsonSerializer.Serialize(s);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.False(restored!.DisableSystemProxy);
    }

    [Fact]
    public void AppSettings_JsonMissingField_DefaultsToTrue()
    {
        var json = "{}";
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.True(restored!.DisableSystemProxy);
    }

    [Fact]
    public void ProxyHandler_DisableTrue_UseProxyFalse()
    {
        var settings = new AppSettings { DisableSystemProxy = true };
        var handler = ProxyTestHelper.CreateHandler(settings);
        var sh = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sh.UseProxy);
    }

    [Fact]
    public void ProxyHandler_DisableFalse_UseProxyTrue()
    {
        var settings = new AppSettings { DisableSystemProxy = false };
        var handler = ProxyTestHelper.CreateHandler(settings);
        var sh = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.True(sh.UseProxy);
    }

    [Fact]
    public void ModelDownloader_CreateHttpClient_Respects_DisableSystemProxy()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"qt_proxy_dl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var provider = Substitute.For<IAppDataProvider>();
            provider.GetAppDataDirectory().Returns(tmp);
            provider.GetLogDirectory().Returns(Path.Combine(tmp, "logs"));
            var logger = Substitute.For<ILogger<ModelDownloader>>();

            var settingsTrue = Options.Create(new AppSettings { DisableSystemProxy = true });
            var dlTrue = new ModelDownloader(provider, logger, settingsTrue);
            var clientTrue = dlTrue.CreateHttpClientForTest();
            var handlerTrue = GetPrimaryHandler(clientTrue);
            Assert.False(((SocketsHttpHandler)handlerTrue).UseProxy);

            var settingsFalse = Options.Create(new AppSettings { DisableSystemProxy = false });
            var dlFalse = new ModelDownloader(provider, logger, settingsFalse);
            var clientFalse = dlFalse.CreateHttpClientForTest();
            var handlerFalse = GetPrimaryHandler(clientFalse);
            Assert.True(((SocketsHttpHandler)handlerFalse).UseProxy);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void ServiceCollectionExtensions_AddTranslationProvider_Configures_ProxyHandler()
    {
        foreach (var disable in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var appSettings = Options.Create(new AppSettings { DisableSystemProxy = disable });
            services.AddSingleton<IOptions<AppSettings>>(appSettings);
            var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
            services.AddTranslationProvider(cfg);

            var sp = services.BuildServiceProvider();

            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var customClient = factory.CreateClient(nameof(QuickTranslate.Infrastructure.Translation.CustomOpenAiTranslationProvider));
            var qwenClient = factory.CreateClient(nameof(QuickTranslate.Infrastructure.Translation.QwenMtTranslationProvider));

            Assert.NotNull(customClient);
            Assert.NotNull(qwenClient);

            var handler = ProxyTestHelper.CreateHandler(new AppSettings { DisableSystemProxy = disable });
            Assert.Equal(!disable, ((SocketsHttpHandler)handler).UseProxy);
        }
    }

    static HttpMessageHandler GetPrimaryHandler(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
            field = typeof(HttpClient).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        return (HttpMessageHandler)field!.GetValue(client)!;
    }
}

internal static class ProxyTestHelper
{
    public static HttpMessageHandler CreateHandler(AppSettings settings)
    {
        var t = Type.GetType("QuickTranslate.Infrastructure.HttpProxyHelper, QuickTranslate.Infrastructure");
        if (t != null)
        {
            var m = t.GetMethod("CreateHandler", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (m != null)
            {
                var result = m.Invoke(null, new object[] { settings });
                if (result is HttpMessageHandler h) return h;
            }
        }
        return new SocketsHttpHandler { UseProxy = !settings.DisableSystemProxy };
    }
}
