using QuickTranslate.Core.Options;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class GlobalHotkeyServiceLifecycleTests
{
    [Fact]
    public void Dispose_ReleasesNativeResources_AndIsIdempotent()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var service = new GlobalHotkeyService();
                service.Dispose();
                service.Dispose();
                Assert.Throws<ObjectDisposedException>(() =>
                    service.Register(1, HotkeyModifiers.Alt, KeyboardKey.D1));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Hotkey service disposal did not finish.");
        Assert.Null(failure);
    }
}
