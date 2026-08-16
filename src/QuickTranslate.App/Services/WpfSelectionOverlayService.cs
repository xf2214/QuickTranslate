using System.Windows.Interop;
using System.Windows.Threading;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.App.Windows;
using QuickTranslate.Platform.UnmanagedMethods;
using QuickTranslate.Platform.Win32;
using QuickTranslate.App.Coordination;

namespace QuickTranslate.App.Services;

public class WpfSelectionOverlayService : ISelectionOverlayService
{
    private readonly IDpiMapper _dpiMapper;
    private readonly Dictionary<MonitorId, (SelectionOverlayWindow window, uint dpiX, uint dpiY)> _windows = new();

    public WpfSelectionOverlayService(IDpiMapper dpiMapper)
    {
        _dpiMapper = dpiMapper;
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

    public void Show(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96)
    {
        RunOnUi(() => ShowCore(physicalBox, monitorId, dpiX, dpiY));
    }

    private void ShowCore(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX, uint dpiY)
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
        RunOnUi(() => UpdateCore(physicalBox, monitorId, dpiX, dpiY));
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
        RunOnUi(() => HideCore(monitorId));
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
        RunOnUi(HideAllCore);
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
