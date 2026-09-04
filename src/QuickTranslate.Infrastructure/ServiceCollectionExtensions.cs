using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

/// <summary>
/// Holds the eagerly-started settings load task and its result.
/// Mechanism: eager-start LoadAsync at singleton registration time (disk + DPAPI CurrentUser decrypt ~50-300ms)
/// without blocking Host.Build via sync-over-async. A HostedService awaits the task during Host.StartAsync
/// (off the UI thread, before hotkeys fire) and patches the cached IOptions AppSettings value in-place.
/// Rationale: Blocking Host.Build with sync-over-async stalls Tray Ready budget under 1s and risks
/// deadlock if LoadAsync ever captures a SynchronizationContext (STA/WPF). Eager Task plus HostedService
/// keeps Build synchronous and fast, while guaranteeing settings are ready before the first hotkey pipeline
/// (coordinators read IOptions at translation time, SettingsWindow is opened lazily after Start; both see the
/// patched instance).
/// </summary>
internal sealed class SettingsLoadState
{
    public Task<AppSettings>? LoadTask { get; set; }
    public AppSettings? Loaded { get; set; }
}

internal sealed class SettingsInitializationService : IHostedService
{
    private readonly SettingsLoadState _state;
    private readonly IServiceProvider _sp;
    private readonly ILogger<SettingsInitializationService> _logger;

    public SettingsInitializationService(SettingsLoadState state, IServiceProvider sp, ILogger<SettingsInitializationService> logger)
    {
        _state = state;
        _sp = sp;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // SettingsManager 的 DI 工厂负责播种 LoadTask（首次解析即启动异步加载）。
        // 历史上只有设置窗口/托盘菜单会解析它 —— 冷启动时无人播种，本服务会在
        // 下一行静默返回：设置从未加载、IOptions 永远是默认值、Debug 日志级别
        // 锁死默认 Information。此处显式解析一次，保证
        // “settings ready before first hotkey”不变量真正成立。
        if (_state.LoadTask is null)
            _sp.GetRequiredService<SettingsManager>();

        if (_state.LoadTask is null) return;
        try
        {
            var loaded = await _state.LoadTask.ConfigureAwait(false);
            _state.Loaded = loaded;

            // Patch the cached IOptions value in-place if it was already created during Build (Wire*Hotkey).
            // If not yet created, the IConfigureOptions below will copy from _state.Loaded on first resolve.
            var opts = _sp.GetService<IOptions<AppSettings>>();
            if (opts is not null)
            {
                var cur = opts.Value;
                // In-place mutation preserves reference identity for any coordinator holding IOptions reference.
                cur.ApplyFrom(loaded);

                // 日志器可能在 Load 完成前就已按默认值创建（Serilog 级别锁死 Information，
                // 见 LoggerConfigurator 注释）。加载完成后把落盘的 Debug 开关同步到运行时。
                LoggingSwitch.SetDebugEnabled(loaded.DebugLogging);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SettingsInitializationService: eager LoadAsync failed (non-fatal, defaults will be used)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSecretStore(configuration);
        services.AddSingleton<IAppDataProvider, DefaultAppDataProvider>();

        // Eager-start LoadAsync without blocking Host.Build: see SettingsLoadState doc above.
        services.AddSingleton<SettingsLoadState>();
        services.AddSingleton<SettingsManager>(sp =>
        {
            var appDataProvider = sp.GetRequiredService<IAppDataProvider>();
            var appDataDir = appDataProvider.GetAppDataDirectory();
            var secretStore = sp.GetRequiredService<ISecretStore>();
            var mgr = new SettingsManager(appDataDir, secretStore);
            var state = sp.GetRequiredService<SettingsLoadState>();
            state.LoadTask = mgr.LoadAsync();
            return mgr;
        });

        // Concrete singleton once; interface forwards to same instance — eliminates fragile (SettingsManager)sm downcast double-registration.
        services.AddSingleton<ISettingsManager>(sp => sp.GetRequiredService<SettingsManager>());
        services.AddHostedService<SettingsInitializationService>();

        services.AddSingleton<SingleInstanceGuard>();

        // No sync-over-async: copy from SettingsLoadState.Loaded if already available; otherwise keep defaults.
        // The HostedService patches the cached IOptions instance after await, so early resolves (Wire* during Build)
        // see defaults briefly but are corrected before the first hotkey can fire (Host.StartAsync awaits the service).
        // SettingsWindow is opened lazily via tray after Start, so it always sees the patched value.
        services.AddSingleton<IConfigureOptions<AppSettings>>(sp =>
        {
            var state = sp.GetRequiredService<SettingsLoadState>();
            return new ConfigureOptions<AppSettings>(opts =>
            {
                var loaded = state.Loaded;
                if (loaded is null) return;
                opts.ApplyFrom(loaded);
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
            // 设置为异步加载：此处可能读到默认值（false），SettingsInitializationService
            // 在 Load 完成后会再次 SetDebugEnabled 同步落盘值。
            LoggingSwitch.SetDebugEnabled(appSettings.DebugLogging);
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
        // 词合理性检查器：ECDICT 词典背书，供 OCR 粘连词拆分（unfuse）做词汇佐证，
        // 防止正常单词被字距缝隙拦腰切断（如 commit→com|mit）。词典未注册（最小容器/
        // 测试）或为空时传 null → 拆分保持纯几何判定；词典为空时 TryLookup 返回 false
        // → 拆分被保守拒绝（宁整勿碎）。
        services.AddSingleton<PaddleOcrV6Engine>(sp =>
        {
            var dictionary = sp.GetService<ILocalDictionary>();
            return new PaddleOcrV6Engine(
                sp.GetRequiredService<IOptions<AppSettings>>(),
                sp.GetRequiredService<IAppDataProvider>(),
                sp.GetRequiredService<ILogger<PaddleOcrV6Engine>>(),
                isPlausibleWord: dictionary is null ? null : word => dictionary.TryLookup(word, "zh", out _));
        });
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
