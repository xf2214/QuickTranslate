using System.Threading;
using System.Windows.Interop;
using QuickTranslate.App.Services;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Platform.Win32;
using Xunit;

namespace QuickTranslate.Tests.App;

public class PopupDipLayoutTests
{
    private static SelectionResult MakeSel(PhysicalRect box) => new(
        Text: "hello",
        ContextLine: "hello world",
        Box: box,
        Kind: SelectionKind.Word,
        Confidence: 0.9f,
        OperationId: Guid.NewGuid(),
        NoTextFound: false);

    private static TranslationResult MakeTrans() => new(
        NormalizedKey: "hello||zh-cn",
        SourceText: "hello",
        TargetText: "你好",
        TargetLanguage: "zh-CN",
        FromCache: false,
        FromDictionary: false,
        NeedsOnline: false);

    [Fact]
    public void Case1_Dpi120_RoundtripMatchesPlacement()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                const uint dpi = 120;
                var dpiMapper = new DpiMapper();
                var fakeMonitor = new MonitorInfo(
                    Id: new MonitorId(new IntPtr(1), @"\\.\DISPLAY0"),
                    DeviceName: @"\\.\DISPLAY0",
                    Bounds: new PhysicalRect(0, 0, 1920, 1080),
                    WorkArea: new PhysicalRect(0, 0, 1920, 1080),
                    DpiX: dpi,
                    DpiY: dpi,
                    IsPrimary: true);
                var monitorSvc = new FakeMonitorServiceForPopup(fakeMonitor);
                var service = new WpfWordPopupService(dpiMapper, monitorSvc);

                var anchorBox = new PhysicalRect(500, 500, 100, 30);
                var popupPreferredSize = new PhysicalSize(
                    (int)Math.Round(320.0 * dpi / 96.0),
                    (int)Math.Round(150.0 * dpi / 96.0));
                var expectedPhysical = PopupPlacement.Place(anchorBox, fakeMonitor.WorkArea, popupPreferredSize);

                service.Show(MakeSel(anchorBox), MakeTrans(), fakeMonitor.Id, anchorBox, dpiX: dpi, dpiY: dpi);

                var inspect = service.Inspect();
                Assert.True(inspect.ContainsKey(fakeMonitor.Id));
                var lastDip = inspect[fakeMonitor.Id].lastDipRect;

                var backToPhysical = dpiMapper.ToPhysical(lastDip, dpi, dpi);

                Assert.True(expectedPhysical.X == backToPhysical.X,
                    $"X differ; expectedPhysical={expectedPhysical}, backPhysical={backToPhysical}");
                Assert.True(expectedPhysical.Y == backToPhysical.Y,
                    $"Y differ; expectedPhysical={expectedPhysical}, backPhysical={backToPhysical}");
                Assert.True(expectedPhysical.Width == backToPhysical.Width,
                    $"Width differ; expectedPhysical={expectedPhysical}, backPhysical={backToPhysical}");

                service.HideAll();

                var field = typeof(WpfWordPopupService).GetField("_windows",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(field);
                var windows = (Dictionary<MonitorId, (WordPopupWindow window, uint dpiX, uint dpiY)>)field!.GetValue(service)!;
                foreach (var v in windows.Values)
                {
                    v.window.Close();
                }

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
    public void Case2_Dpi96_Baseline()
    {
        bool passed = false;
        Exception? error = null;

        Thread thread = new(() =>
        {
            try
            {
                const uint dpi = 96;
                var dpiMapper = new DpiMapper();
                var fakeMonitor = new MonitorInfo(
                    Id: new MonitorId(new IntPtr(2), @"\\.\DISPLAY1"),
                    DeviceName: @"\\.\DISPLAY1",
                    Bounds: new PhysicalRect(0, 0, 1920, 1080),
                    WorkArea: new PhysicalRect(0, 0, 1920, 1040),
                    DpiX: dpi,
                    DpiY: dpi,
                    IsPrimary: true);
                var monitorSvc = new FakeMonitorServiceForPopup(fakeMonitor);
                var service = new WpfWordPopupService(dpiMapper, monitorSvc);

                var anchorBox = new PhysicalRect(100, 100, 100, 30);
                var popupPreferredSize = new PhysicalSize(320, 150);
                var expectedPhysical = PopupPlacement.Place(anchorBox, fakeMonitor.WorkArea, popupPreferredSize);

                service.Show(MakeSel(anchorBox), MakeTrans(), fakeMonitor.Id, anchorBox, dpiX: dpi, dpiY: dpi);

                var inspect = service.Inspect();
                var lastDip = inspect[fakeMonitor.Id].lastDipRect;
                var backPhysical = dpiMapper.ToPhysical(lastDip, dpi, dpi);

                Assert.Equal(expectedPhysical.X, backPhysical.X);
                Assert.Equal(expectedPhysical.Y, backPhysical.Y);
                Assert.Equal(expectedPhysical.Width, backPhysical.Width);

                service.HideAll();

                var field = typeof(WpfWordPopupService).GetField("_windows",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var windows = (Dictionary<MonitorId, (WordPopupWindow window, uint dpiX, uint dpiY)>)field!.GetValue(service)!;
                foreach (var v in windows.Values)
                {
                    v.window.Close();
                }

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
    public void Case3_PopupWindow_WS_EX_NOACTIVATE_And_TOPMOST_Set()
    {
        bool passed = false;
        Exception? error = null;

        const uint WS_EX_NOACTIVATE = 0x08000000;
        const uint WS_EX_TOPMOST = 0x00000008;

        Thread thread = new(() =>
        {
            try
            {
                var window = new WordPopupWindow();
                new WindowInteropHelper(window).EnsureHandle();

                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                IntPtr styleIntPtr = WindowStyleHelper.GetExtendedStyle(hwnd);
                uint style = unchecked((uint)(ulong)styleIntPtr.ToInt64());

                Assert.True((style & WS_EX_NOACTIVATE) == WS_EX_NOACTIVATE,
                    $"WS_EX_NOACTIVATE not set. Style=0x{style:X8}");
                Assert.True((style & WS_EX_TOPMOST) == WS_EX_TOPMOST,
                    $"WS_EX_TOPMOST not set. Style=0x{style:X8}");

                window.Close();
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

public class FakeMonitorServiceForPopup : IMonitorService
{
    private readonly MonitorInfo _info;

    public FakeMonitorServiceForPopup(MonitorInfo info) { _info = info; }

    public IReadOnlyList<MonitorInfo> EnumerateMonitors() => new[] { _info };

    public MonitorInfo? TryGetMonitorFromPoint(PhysicalPoint pt) => _info;

    public MonitorInfo? TryGetPrimary() => _info;

    public MonitorId MonitorFromWindow(IntPtr hwnd) => _info.Id;
}
