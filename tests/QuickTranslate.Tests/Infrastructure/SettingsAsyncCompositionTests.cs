using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Persistence;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

/// <summary>
/// Validates the async DI composition fix:
/// - no sync-over-async in ServiceCollectionExtensions factories,
/// - SettingsManager is registered once as concrete and forwarded,
/// - settings are guaranteed loaded before first consumer use via HostedService.
/// </summary>
public class SettingsAsyncCompositionTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _logDir;

    public SettingsAsyncCompositionTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"qt_async_comp_{Guid.NewGuid():N}");
        _logDir = Path.Combine(_tmpDir, "logs");
        Directory.CreateDirectory(_tmpDir);
        Directory.CreateDirectory(_logDir);
        Environment.SetEnvironmentVariable("QUICKTRANSLATE_APPDATA", _tmpDir);
        Environment.SetEnvironmentVariable("QUICKTRANSLATE_LOGDIR", _logDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("QUICKTRANSLATE_APPDATA", null);
        Environment.SetEnvironmentVariable("QUICKTRANSLATE_LOGDIR", null);
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public async Task Provider_Resolves_SameSingleton_And_OptionsReflectLoadedSettings()
    {
        var config = new ConfigurationBuilder().Build();
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((ctx, services) =>
            {
                services.AddInfrastructure(config);
            })
            .Build();

        // StartAsync must await SettingsInitializationService (eager LoadAsync).
        await host.StartAsync();

        var mgrConcrete = host.Services.GetRequiredService<SettingsManager>();
        var mgrInterface = host.Services.GetRequiredService<ISettingsManager>();
        Assert.Same(mgrConcrete, mgrInterface);

        // IOptions should reflect the loaded (or default-initialized) settings, not stale defaults.
        var opts = host.Services.GetRequiredService<IOptions<AppSettings>>().Value;
        Assert.NotNull(opts);
        Assert.Equal(HotkeyModifiers.Alt, opts.WordHotkey.Modifiers);
        Assert.Equal(KeyboardKey.D1, opts.WordHotkey.Key);
        Assert.Equal(HotkeyModifiers.Alt, opts.BlockHotkey.Modifiers);
        Assert.Equal(KeyboardKey.D2, opts.BlockHotkey.Key);
        Assert.Equal("zh-CN", opts.TargetLanguage);

        // Verify the settings file was actually created by the eager LoadAsync (not deferred).
        var settingsFile = Path.Combine(_tmpDir, "settings.json");
        Assert.True(File.Exists(settingsFile));

        // Simulate consumer funneling through async accessor: second LoadAsync returns same values.
        var reloaded = await mgrConcrete.LoadAsync();
        Assert.Equal(opts.TargetLanguage, reloaded.TargetLanguage);
        Assert.Equal(opts.WordHotkey.Modifiers, reloaded.WordHotkey.Modifiers);

        await host.StopAsync();
        host.Dispose();
    }

    [Fact]
    public async Task Build_DoesNotBlock_OnDiskDpapi()
    {
        var config = new ConfigurationBuilder().Build();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((ctx, services) => services.AddInfrastructure(config))
            .Build();
        sw.Stop();

        // Build should return quickly and not block on DPAPI/disk (<500ms even on slow CI).
        Assert.True(sw.ElapsedMilliseconds < 500, $"Host.Build blocked for {sw.ElapsedMilliseconds}ms, expected <500ms");

        // Cleanup: start to ensure hosted service completes, then stop.
        await host.StartAsync();
        await host.StopAsync();
        host.Dispose();
    }
}
