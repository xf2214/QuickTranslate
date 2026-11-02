using NSubstitute;
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
    [Fact]
    public void CleanWordPopupService_RemovesStaleMonitors_AndClosesWindows()
    {
        RunOnSta(() =>
        {
            var dpiMapper = new QuickTranslate.Platform.Win32.DpiMapper();
            var mid1 = new QuickTranslate.Core.Geometry.MonitorId(new IntPtr(1), @"\\.\DISPLAY1");
            var mid2 = new QuickTranslate.Core.Geometry.MonitorId(new IntPtr(2), @"\\.\DISPLAY2");
            var m1 = new QuickTranslate.Core.Geometry.MonitorInfo(mid1, @"\\.\DISPLAY1", new QuickTranslate.Core.Geometry.PhysicalRect(0, 0, 1920, 1080), new QuickTranslate.Core.Geometry.PhysicalRect(0, 0, 1920, 1080), 96, 96, true);
            var m2 = new QuickTranslate.Core.Geometry.MonitorInfo(mid2, @"\\.\DISPLAY2", new QuickTranslate.Core.Geometry.PhysicalRect(1920, 0, 1920, 1080), new QuickTranslate.Core.Geometry.PhysicalRect(1920, 0, 1920, 1080), 144, 144, false);

            var monSvc = NSubstitute.Substitute.For<QuickTranslate.Core.Abstractions.IMonitorService>();
            monSvc.EnumerateMonitors().Returns(new[] { m1, m2 });
            var wordSvc = new QuickTranslate.App.Services.WpfWordPopupService(dpiMapper, monSvc, Microsoft.Extensions.Options.Options.Create(new QuickTranslate.Core.Options.AppSettings()));

            var field = typeof(QuickTranslate.App.Services.WpfWordPopupService).GetField("_windows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(field);
            var dict = (Dictionary<QuickTranslate.Core.Geometry.MonitorId, (QuickTranslate.App.Windows.WordPopupWindow window, uint dpiX, uint dpiY)>)field!.GetValue(wordSvc)!;

            var w1 = (QuickTranslate.App.Windows.WordPopupWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(QuickTranslate.App.Windows.WordPopupWindow));
            var w2 = (QuickTranslate.App.Windows.WordPopupWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(QuickTranslate.App.Windows.WordPopupWindow));
            dict[mid1] = (w1, 96, 96);
            dict[mid2] = (w2, 144, 144);

            // 拔掉 DISPLAY2，只剩 DISPLAY1
            StartupDisplayProbe.CleanWordPopupService(wordSvc, new[] { m1 });

            Assert.Single(dict);
            Assert.Contains(mid1, dict.Keys);
            Assert.DoesNotContain(mid2, dict.Keys);

            // w1 is uninitialized mock
        });
    }

    [Fact]
    public void CleanWordPopupService_RemovesMonitorsWithChangedDpi()
    {
        RunOnSta(() =>
        {
            var dpiMapper = new QuickTranslate.Platform.Win32.DpiMapper();
            var mid1 = new QuickTranslate.Core.Geometry.MonitorId(new IntPtr(1), @"\\.\DISPLAY1");
            var m1 = new QuickTranslate.Core.Geometry.MonitorInfo(mid1, @"\\.\DISPLAY1", new QuickTranslate.Core.Geometry.PhysicalRect(0, 0, 1920, 1080), new QuickTranslate.Core.Geometry.PhysicalRect(0, 0, 1920, 1080), 96, 96, true);

            var monSvc = NSubstitute.Substitute.For<QuickTranslate.Core.Abstractions.IMonitorService>();
            monSvc.EnumerateMonitors().Returns(new[] { m1 });
            var wordSvc = new QuickTranslate.App.Services.WpfWordPopupService(dpiMapper, monSvc, Microsoft.Extensions.Options.Options.Create(new QuickTranslate.Core.Options.AppSettings()));

            var field = typeof(QuickTranslate.App.Services.WpfWordPopupService).GetField("_windows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var dict = (Dictionary<QuickTranslate.Core.Geometry.MonitorId, (QuickTranslate.App.Windows.WordPopupWindow window, uint dpiX, uint dpiY)>)field!.GetValue(wordSvc)!;

            var w1 = (QuickTranslate.App.Windows.WordPopupWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(QuickTranslate.App.Windows.WordPopupWindow));
            dict[mid1] = (w1, 144, 144); // 之前认知是 144

            // 现在活跃显示器变为了 96 DPI
            StartupDisplayProbe.CleanWordPopupService(wordSvc, new[] { m1 });

            // DPI 不一致，窗口缓存被清理，等待下次按新 DPI 重建
            Assert.Empty(dict);
        });
    }

    [Fact]
    public void CleanBlockPopupService_ResetsDpiCognition_AndMonitor()
    {
        RunOnSta(() =>
        {
            var dpiMapper = new QuickTranslate.Platform.Win32.DpiMapper();
            var monSvc = NSubstitute.Substitute.For<QuickTranslate.Core.Abstractions.IMonitorService>();
            var blockSvc = new QuickTranslate.App.Services.WpfBlockPopupService(dpiMapper, monSvc, Microsoft.Extensions.Options.Options.Create(new QuickTranslate.Core.Options.AppSettings()));

            var type = typeof(QuickTranslate.App.Services.WpfBlockPopupService);
            type.GetField("_lastMonitorId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(blockSvc, new QuickTranslate.Core.Geometry.MonitorId(new IntPtr(1), @"\\.\DISPLAY1"));
            type.GetField("_lastDpiX", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(blockSvc, 144u);
            type.GetField("_lastDpiY", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(blockSvc, 144u);

            StartupDisplayProbe.CleanBlockPopupService(blockSvc);

            var monId = (QuickTranslate.Core.Geometry.MonitorId)type.GetField("_lastMonitorId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(blockSvc)!;
            var dpiX = (uint)type.GetField("_lastDpiX", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(blockSvc)!;
            var dpiY = (uint)type.GetField("_lastDpiY", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(blockSvc)!;

            Assert.Equal(QuickTranslate.Core.Geometry.MonitorId.Empty, monId);
            Assert.Equal(0u, dpiX);
            Assert.Equal(0u, dpiY);
        });
    }
}

