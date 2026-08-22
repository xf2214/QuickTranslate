using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Options;
using QuickTranslate.App.Windows;
using QuickTranslate.Platform.UnmanagedMethods;
using QuickTranslate.Platform.Win32;
using QuickTranslate.App.Coordination;

namespace QuickTranslate.App.Services;

public class WpfSelectionOverlayService : ISelectionOverlayService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly IOptions<AppSettings>? _appSettings;
    private readonly Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)> _windows = new();

    public WpfSelectionOverlayService(IDpiMapper dpiMapper, IOptions<AppSettings>? appSettings = null)
    {
        _dpiMapper = dpiMapper;
        _appSettings = appSettings;
    }

    private static void RunOnUi(Action a)
    {
        // 优先使用 WPF 主应用的 Dispatcher（确保跨线程回到 STA UI 线程）。
        // 测试 / 无头场景中 Application.Current 可能为 null：
        // 此时使用当前线程的 Dispatcher——xunit STA 线程会创建/关联一个 Dispatcher，
        // Dispatcher.Invoke 在同一 STA 线程安全执行 WPF Window 操作。
        // 只有当当前线程也没有 Dispatcher（MTA）时才抛，避免进入后台线程非法操作 WPF。
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher == null)
        {
            throw new InvalidOperationException(
                "No WPF dispatcher is available (both Application.Current and Dispatcher.CurrentDispatcher are null). " +
                "WPF UI services cannot operate on an MTA thread without a host Application instance.");
        }

        if (dispatcher.CheckAccess()) a();
        else dispatcher.Invoke(DispatcherPriority.Normal, a);
    }

    private static void RunOnUiAsync(Action a)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher == null)
        {
            throw new InvalidOperationException(
                "No WPF dispatcher is available (both Application.Current and Dispatcher.CurrentDispatcher are null). " +
                "WPF UI services cannot operate on an MTA thread without a host Application instance.");
        }

        if (dispatcher.CheckAccess()) a();
        else _ = dispatcher.InvokeAsync(a, DispatcherPriority.Normal);
    }

    public void Show(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96, bool preview = false)
    {
        // Show keeps blocking Invoke: strict ordering required — callers (coordinators) rely on
        // HWND creation / EnsureHandle + measured placement completing before OCR/selection
        // continues and before any subsequent Update/Hide. Non-blocking here would race
        // IsVisible/Handle checks and cause flicker/misplaced overlay under contention.
        RunOnUi(() => ShowCore(physicalBox, monitorId, dpiX, dpiY, preview));
    }

    private void ShowCore(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX, uint dpiY, bool preview)
    {
        bool needsRecreate = true;
        if (_windows.TryGetValue(monitorId, out var entry))
        {
            if (PerMonitorDpiHelpers.AreClose(entry.dpiX, dpiX) && PerMonitorDpiHelpers.AreClose(entry.dpiY, dpiY))
            {
                needsRecreate = false;
            }
            else
            {
                entry.window.Close();
                _windows.Remove(monitorId);
            }
        }

        SelectionOverlayWindow window;
        if (needsRecreate || !_windows.TryGetValue(monitorId, out entry))
        {
            window = new SelectionOverlayWindow();
            _windows[monitorId] = (window, dpiX, dpiY);
        }
        else
        {
            window = entry.window;
            _windows[monitorId] = (window, dpiX, dpiY);
        }

        window.SetDebugBoxMode(_appSettings?.Value.DebugOverlayMode == true);
        window.SetPreviewMode(preview);
        window.ApplyPhysicalLayout(physicalBox, dpiX, dpiY);

        if (!window.IsVisible)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                hwnd = new WindowInteropHelper(window).EnsureHandle();
            }
            window.Show();
        }
    }

    public void Update(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96)
    {
        RunOnUiAsync(() => UpdateCore(physicalBox, monitorId, dpiX, dpiY));
    }

    private void UpdateCore(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX, uint dpiY)
    {
        if (_windows.TryGetValue(monitorId, out var entry))
        {
            entry.window.ApplyPhysicalLayout(physicalBox, dpiX, dpiY);
            _windows[monitorId] = (entry.window, dpiX, dpiY);

            IntPtr hwnd = new WindowInteropHelper(entry.window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                User32.SetWindowPos(
                    hwnd,
                    User32.HWND_TOPMOST,
                    0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            }
        }
    }

    public void Hide(MonitorId monitorId)
    {
        RunOnUiAsync(() => HideCore(monitorId));
    }

    private void HideCore(MonitorId monitorId)
    {
        if (_windows.TryGetValue(monitorId, out var entry))
        {
            entry.window.Hide();
        }
    }

    public void HideAll()
    {
        RunOnUiAsync(HideAllCore);
    }

    private void HideAllCore()
    {
        foreach (var kvp in _windows)
        {
            kvp.Value.window.Hide();
        }
    }

    public IReadOnlyDictionary<MonitorId, (IntPtr hwnd, DipRect lastDipRect)> Inspect()
    {
        var result = new Dictionary<MonitorId, (IntPtr, DipRect)>();
        foreach (var kvp in _windows)
        {
            IntPtr hwnd = new WindowInteropHelper(kvp.Value.window).Handle;
            result[kvp.Key] = (hwnd, kvp.Value.window.LastLayoutRect);
        }
        return result;
    }
}
