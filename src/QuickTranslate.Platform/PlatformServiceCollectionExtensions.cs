using Microsoft.Extensions.DependencyInjection;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Platform.Tray;
using QuickTranslate.Platform.Win32;

namespace QuickTranslate.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<ICursorService, CursorService>();
        services.AddSingleton<IMonitorService, MonitorService>();
        services.AddSingleton<IDpiMapper, DpiMapper>();

        services.AddSingleton<IAppLifecycle, DefaultAppLifecycle>();

        return services;
    }
}
