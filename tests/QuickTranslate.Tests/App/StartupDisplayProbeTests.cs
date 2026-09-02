using System.Runtime.InteropServices;
using System.Windows.Interop;
using QuickTranslate.App.Bootstrap;
using QuickTranslate.Platform.UnmanagedMethods;
using Xunit;

namespace QuickTranslate.Tests.App;

/// <summary>
/// 回归守护：拓扑监听（DisplayWatcher）消息窗口必须不可见、无标题栏。
/// 历史 Bug：HwndSourceParameters 默认样式为 WS_OVERLAPPEDWINDOW|WS_VISIBLE，
/// 启动时在屏幕左上角弹出一个 136x39 的空白标题栏窗口（"启动弹出窗口"）。
/// </summary>
public class StartupDisplayProbeTests
{
    private const int GWL_STYLE = -16;
    private const long WS_VISIBLE = 0x10000000;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [Fact]
    public void WatcherWindow_IsInvisible_AtStartup()
    {
        RunOnSta(() =>
        {
            HwndSource? source = null;
            try
            {
                source = new HwndSource(StartupDisplayProbe.CreateWatcherParameters());
                IntPtr hwnd = source.Handle;
                Assert.NotEqual(IntPtr.Zero, hwnd);

                // WS_VISIBLE 必须清除：默认样式会创建可见的空白标题栏窗口（启动弹窗根因）。
                // 注：WPF 对顶层 HwndSource 强制 OR 上 WS_CAPTION（SetWindowRgn 兼容），
                // 该位对不可见窗口无影响，故只守护可见性。
                long style = User32.GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();
                Assert.Equal(0, style & WS_VISIBLE);
                Assert.False(IsWindowVisible(hwnd), "DisplayWatcher must not be visible at startup");
            }
            finally
            {
                source?.Dispose();
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                action();
                passed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        }
        Assert.True(passed);
    }
}
