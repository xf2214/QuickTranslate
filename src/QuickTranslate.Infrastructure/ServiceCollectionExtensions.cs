using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Cache;
using QuickTranslate.Infrastructure.Coordination;
using QuickTranslate.Infrastructure.Logging;
using QuickTranslate.Infrastructure.Ocr;
using QuickTranslate.Infrastructure.Options;
using QuickTranslate.Infrastructure.Persistence;
using QuickTranslate.Infrastructure.Security;
using QuickTranslate.Infrastructure.Services;
using QuickTranslate.Infrastructure.SingleInstance;
using QuickTranslate.Infrastructure.Translation;

namespace QuickTranslate.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSecretStore(configuration);
        services.AddSingleton<IAppDataProvider, DefaultAppDataProvider>();

        services.AddSingleton<ISettingsManager>(sp =>
        {
            var appDataProvider = sp.GetRequiredService<IAppDataProvider>();
            var appDataDir = appDataProvider.GetAppDataDirectory();
            var secretStore = sp.GetRequiredService<ISecretStore>();
            var sm = new SettingsManager(appDataDir, secretStore);
            var appSettings = sm.LoadAsync().GetAwaiter().GetResult();
            return sm;
        });

        services.AddSingleton(sp =>
        {
            var sm = sp.GetRequiredService<ISettingsManager>();
            return (SettingsManager)sm;
        });

        services.AddSingleton<SingleInstanceGuard>();

        services.AddSingleton<IConfigureOptions<AppSettings>>(sp =>
        {
            var settingsManager = sp.GetRequiredService<ISettingsManager>();
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
                opts.EnableTextToSpeech = appSettings.EnableTextToSpeech;
                opts.TranslationProvider = appSettings.TranslationProvider;
                opts.CustomLlmBaseUrl = appSettings.CustomLlmBaseUrl;
                opts.CustomLlmModel = appSettings.CustomLlmModel;
                opts.CustomLlmMaxContextLines = appSettings.CustomLlmMaxContextLines;
                opts.ResolvedApiKey = appSettings.ResolvedApiKey;
            });
        });

        services.AddSingleton<ModelVersionVerifier>();
        services.AddSingleton<IModelDownloader, ModelDownloader>();

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
            builder.SetMinimumLevel(global::Microsoft.Extensions.Logging.LogLevel.Trace);
        });

        services.AddSingleton<ILoggerProvider>(sp =>
        {
            var appSettings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppSettings>>().Value;
            LoggingSwitch.IsDebugEnabled = appSettings.DebugLogging;
            var innerFactory = sp.GetRequiredService<ILoggerFactory>();
            var inner = new SerilogAppLoggingProvider(innerFactory);
            return new FilteringLoggerProvider(inner, level =>
            {
                if (LoggingSwitch.IsDebugEnabled)
                    return true;
                return level >= global::Microsoft.Extensions.Logging.LogLevel.Information;
            });
        });

        AddOcrEngines(services);
        AddSelectionCore(services);
        AddCacheAndRouter(services);
        AddSqliteL2Cache(services, configuration);
        AddTranslationProvider(services, configuration);
        AddCoordinationServices(services);

        return services;
    }

    /// <summary>
    /// 接入 SQLite L2 持久缓存：L1 内存缓存重启即失效，若不接 L2，
    /// 每次重启后所有在线译文都要重新请求（每次数百毫秒~秒级网络耗时）。
    /// 落盘目录覆盖为 AppData（安装目录可能不可写）。
    /// </summary>
    private static void AddSqliteL2Cache(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSqliteCache(configuration);
        services.AddOptions<SqliteCacheOptions>()
            .Configure<IAppDataProvider>((opts, appData) =>
                opts.DataDirectory = appData.GetAppDataDirectory());
    }

    public static IServiceCollection AddCoordinationServices(this IServiceCollection services)
    {
        services.AddSingleton<BlockRetryCoordinator>();
        return services;
    }

    public static IServiceCollection AddTranslationProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<QwenMtOptions>()
            .Bind(configuration.GetSection("Qwen"))
            .ValidateDataAnnotations()
            .PostConfigure<IOptions<AppSettings>>((opts, appSettings) =>
            {
                var resolvedKey = appSettings.Value.ResolvedApiKey;
                if (!string.IsNullOrWhiteSpace(resolvedKey))
                {
                    opts.ApiKey = resolvedKey;
                }
            });

        // 自定义大模型（OpenAI 兼容）：配置实时读取 AppSettings 单例（设置窗口保存后立即生效），
        // 因此不经过 IOptions 快照。请求 URL 使用绝对地址，不依赖 HttpClient.BaseAddress。
        services.AddHttpClient<CustomOpenAiTranslationProvider>();

        services.AddHttpClient<QwenMtTranslationProvider>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<QwenMtOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.BaseAddress))
            {
                client.BaseAddress = new Uri(opts.BaseAddress.EndsWith('/') ? opts.BaseAddress : opts.BaseAddress + "/");
            }
            client.Timeout = opts.Timeout;
        });

        // ITranslationProvider = 分发器：按设置在自定义大模型 / Qwen-MT 之间路由
        services.AddSingleton<ITranslationProvider, TranslationProviderDispatcher>();

        return services;
    }

    public static IServiceCollection AddCacheAndRouter(this IServiceCollection services)
    {
        services.AddSingleton<ITranslationCache>(_ => new MemoryLruTranslationCache(capacity: 1000));
        // ECDICT-lite 词典：优先读取 packed 高频词 + 用户 overlay，再兜底 stub 59 词
        // 若 ECDICT 加载路径异常（如 packed 缺失），不影响应用启动——内部自动回退到 stub
        services.AddSingleton<ILocalDictionary, EcdictLiteDictionary>();
        services.AddSingleton<ITranslationRouter, DefaultTranslationRouter>(sp =>
        {
            var cache = sp.GetRequiredService<ITranslationCache>();
            var dict = sp.GetRequiredService<ILocalDictionary>();
            var provider = sp.GetRequiredService<ITranslationProvider>();
            var settings = sp.GetRequiredService<IOptions<AppSettings>>();
            var logger = sp.GetRequiredService<ILogger<DefaultTranslationRouter>>();
            var l2 = TryResolveL2Cache(sp, logger);
            return new DefaultTranslationRouter(cache, dict, provider, settings, logger, l2);
        });
        return services;
    }

    /// <summary>
    /// L2 是可选增强：若 SQLite 构造失败（磁盘/权限/原生库等），
    /// 降级为无 L2 运行，而不是让整个翻译管线在 DI 解析时崩溃。
    /// </summary>
    private static ITranslationL2Cache? TryResolveL2Cache(IServiceProvider sp, ILogger logger)
    {
        try
        {
            return sp.GetService<ITranslationL2Cache>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "L2 cache unavailable (construction failed); continuing without it");
            return null;
        }
    }

    public static IServiceCollection AddSqliteCache(this IServiceCollection services, IConfiguration? config = null)
    {
        services.AddOptions<SqliteCacheOptions>();
        if (config != null) services.Configure<SqliteCacheOptions>(config.GetSection("SqliteCache"));
        services.AddSingleton<ITranslationL2Cache, SqliteTranslationCache>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SqliteCacheOptions>>().Value;
            var dir = Path.IsPathRooted(opts.DataDirectory) ? opts.DataDirectory
                        : Path.Combine(AppContext.BaseDirectory, opts.DataDirectory);
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, opts.FileName);
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SqliteTranslationCache>();
            return ActivatorUtilities.CreateInstance<SqliteTranslationCache>(sp, dbPath, opts.MaxKeepRows, logger);
        });
        return services;
    }

    public static IServiceCollection AddSelectionCore(this IServiceCollection services)
    {
        services.AddSingleton<IWordBoxResolver, DefaultWordBoxResolver>();
        services.AddSingleton<IWordSelector, WordSelector>();
        services.AddSingleton<IBlockSelector, DefaultBlockSelector>();
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

    public static IServiceCollection AddSecretStore(this IServiceCollection services, IConfiguration? config = null)
    {
        services.AddOptions<SecretStoreOptions>();
        if (config != null) services.Configure<SecretStoreOptions>(config.GetSection("SecretStore"));
        services.AddSingleton<ISecretStore, DpapiCurrentUserSecretStore>();
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
