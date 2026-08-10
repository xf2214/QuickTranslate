using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Logging;
using QuickTranslate.Infrastructure.Ocr;
using QuickTranslate.Infrastructure.Persistence;
using QuickTranslate.Infrastructure.SingleInstance;

namespace QuickTranslate.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAppDataProvider, DefaultAppDataProvider>();

        services.AddSingleton(sp =>
        {
            var appDataProvider = sp.GetRequiredService<IAppDataProvider>();
            var appDataDir = appDataProvider.GetAppDataDirectory();
            return new SettingsManager(appDataDir);
        });

        services.AddSingleton<SingleInstanceGuard>();

        services.AddSingleton<IConfigureOptions<AppSettings>>(sp =>
        {
            var settingsManager = sp.GetRequiredService<SettingsManager>();
            var appSettings = settingsManager.LoadAsync().GetAwaiter().GetResult();
            return new ConfigureOptions<AppSettings>(opts =>
            {
                opts.WordHotkey = appSettings.WordHotkey;
                opts.BlockHotkey = appSettings.BlockHotkey;
                opts.TargetLanguage = appSettings.TargetLanguage;
                opts.TranslationQuality = appSettings.TranslationQuality;
                opts.StartWithWindows = appSettings.StartWithWindows;
                opts.CloseOnOutsideClick = appSettings.CloseOnOutsideClick;
                opts.DebugLogging = appSettings.DebugLogging;
            });
        });

        services.AddSingleton<ILoggerFactory>(sp =>
        {
            var appSettings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
            var appDataProvider = sp.GetRequiredService<IAppDataProvider>();
            var logDir = appDataProvider.GetLogDirectory();
            return LoggerConfigurator.Configure(appSettings, logDir);
        });

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(global::Microsoft.Extensions.Logging.LogLevel.Information);
        });

        services.AddSingleton<ILoggerProvider, SerilogAppLoggingProvider>();

        AddOcrEngines(services);

        return services;
    }

    public static IServiceCollection AddOcrEngines(this IServiceCollection services)
    {
        services.AddSingleton<PaddleOcrV6Engine>();
        services.AddSingleton<MockOcrEngine>();

        services.AddSingleton<IOcrEngine>(sp =>
        {
            var paddleEngine = sp.GetRequiredService<PaddleOcrV6Engine>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("QuickTranslate.Infrastructure.ServiceCollectionExtensions");

            if (paddleEngine.IsAvailable)
            {
                return paddleEngine;
            }

            logger.LogWarning("PP-OCRv6 models not found, MockOcrEngine fallback.");
            return sp.GetRequiredService<MockOcrEngine>();
        });

        return services;
    }
}

internal class SerilogAppLoggingProvider : ILoggerProvider
{
    private readonly ILoggerFactory _factory;

    public SerilogAppLoggingProvider(ILoggerFactory factory)
    {
        _factory = factory;
    }

    public ILogger CreateLogger(string categoryName) => _factory.CreateLogger(categoryName);

    public void Dispose()
    {
    }
}
