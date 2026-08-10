using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.App.Bootstrap;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Platform.Tray;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class TrayServiceContractTests
{
    [Fact]
    public void ITrayIconService_HasRequiredMembers()
    {
        var type = typeof(ITrayIconService);

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        Assert.Contains("Show", methods);
        Assert.Contains("Hide", methods);
        Assert.Contains("ShowNotification", methods);

        var showNotification = type.GetMethod("ShowNotification",
            new[] { typeof(string), typeof(string), typeof(ToolTipIcon) });
        Assert.NotNull(showNotification);

        var parameters = showNotification.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal("title", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal("message", parameters[1].Name);
        Assert.Equal(typeof(string), parameters[1].ParameterType);
        Assert.Equal("icon", parameters[2].Name);
        Assert.Equal(typeof(ToolTipIcon), parameters[2].ParameterType);
        Assert.True(parameters[2].HasDefaultValue);
        Assert.Equal(ToolTipIcon.Info, parameters[2].DefaultValue);
    }

    [Fact]
    public void ToolTipIcon_Enum_HasAllRequiredValues()
    {
        var values = Enum.GetValues(typeof(ToolTipIcon)).Cast<ToolTipIcon>().ToList();

        Assert.Contains(ToolTipIcon.None, values);
        Assert.Contains(ToolTipIcon.Info, values);
        Assert.Contains(ToolTipIcon.Warning, values);
        Assert.Contains(ToolTipIcon.Error, values);

        Assert.Equal(0, (int)ToolTipIcon.None);
        Assert.Equal(1, (int)ToolTipIcon.Info);
        Assert.Equal(2, (int)ToolTipIcon.Warning);
        Assert.Equal(4, values.Count);
    }

    [Fact]
    public void WinFormsTrayIconService_CanBeConstructed_WithoutApplicationRun()
    {
        int shutdownCalledWith = -1;
        var appLifecycle = new DefaultAppLifecycle(code => { shutdownCalledWith = code; });
        var logger = NullLogger<WinFormsTrayIconService>.Instance;

        WinFormsTrayIconService service;
        try
        {
            service = new WinFormsTrayIconService(appLifecycle, logger);
        }
        catch (Exception ex)
        {
            Assert.Fail($"WinFormsTrayIconService constructor threw unexpectedly: {ex}");
            return;
        }

        Assert.NotNull(service);
        Assert.IsAssignableFrom<ITrayIconService>(service);
        Assert.IsAssignableFrom<IDisposable>(service);

        service.Dispose();
    }
}
