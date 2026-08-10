using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            })
            .ConfigureLogging((context, logging) =>
            {
            })
            .Build();

        return host;
    }
}
