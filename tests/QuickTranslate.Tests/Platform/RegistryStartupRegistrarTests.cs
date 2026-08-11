using QuickTranslate.Core.Abstractions;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class RegistryStartupRegistrarTests
{
    private IStartupRegistrar CreateRegistrar()
    {
        return new RegistryStartupRegistrar();
    }

    [Fact]
    public void Enable_ThenDisable_IsEnabledFalse()
    {
        var registrar = CreateRegistrar();

        registrar.Disable();

        registrar.Enable();
        Assert.True(registrar.IsEnabled, "IsEnabled should be true after Enable");

        var cmdLine = registrar.GetCommandLine();
        Assert.NotNull(cmdLine);
        Assert.NotEmpty(cmdLine);

        registrar.Enable("--second-call");
        var cmdLine2 = registrar.GetCommandLine();
        Assert.Contains("--second-call", cmdLine2);

        registrar.Disable();
        Assert.False(registrar.IsEnabled, "IsEnabled should be false after Disable");
    }

    [Fact]
    public void Enable_DoesNotRequireAdmin()
    {
        var registrar = CreateRegistrar();

        try
        {
            registrar.Disable();
            registrar.Enable("--test");
            var enabled = registrar.IsEnabled;
            Assert.True(enabled);
        }
        catch (System.Security.SecurityException)
        {
            Assert.Fail("HKCU Run operations should not throw SecurityException for normal users");
        }
        finally
        {
            try { registrar.Disable(); } catch { }
        }
    }

    [Fact]
    public void Disabled_NoThrow_MissingKey()
    {
        var registrar = CreateRegistrar();

        try { registrar.Disable(); } catch { }

        var ex = Record.Exception(() => registrar.Disable());
        Assert.Null(ex);

        ex = Record.Exception(() => registrar.Disable());
        Assert.Null(ex);
    }
}
