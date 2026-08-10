using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Infrastructure;
using QuickTranslate.Platform;

namespace QuickTranslate.App.Bootstrap;

public static class AppHost
{
    public static IHost Build()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddInfrastructure(context.Configuration);
                services.AddPlatform();
                services.AddSingleton<ITrayIconService, WinFormsTrayIconService>();
            })
            .ConfigureLogging((context, logging) =>
            {
            })
            .Build();

        return host;
    }
}
