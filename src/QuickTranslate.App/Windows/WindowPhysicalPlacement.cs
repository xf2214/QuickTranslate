using System.Windows;
using System.Windows.Interop;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.App.Windows;

/// <summary>
/// WPF 窗口物理像素定位：以 HWND 级 SetWindowPos 直接写入物理坐标，绕开
/// Window.Left/Top 的 DIP 换算依赖"窗口创建时刻 DPI 认知"的时序问题。
/// 高缩放单屏（启动后改分辨率/缩放）、混合 DPI 多屏、启动后接拔显示器等场景下，
/// WPF 的初始 DPI 认知 ≠ 目标屏 DPI，DIP 定位会按错误比例缩放且不触发 WM_DPICHANGED。
///
/// 顺序约定（重要）：先写 WPF DIP 属性，再 SetWindowPos 物理覆盖 —— 保证物理写入是
/// 最后一个生效的定位调用；WPF 后续经 WM_MOVE/WM_SIZE 从实际 HWND 几何回读同步。
/// 若随后 WM_DPICHANGED 到达，由各窗口自身的 OnDpiChanged 以 LastPhysicalBox 收敛。
/// </summary>
public static class WindowPhysicalPlacement
{
    /// <summary>
    /// 以物理像素定位窗口（含尺寸）。padDip 为 DIP 单位的扩展边距（与既有
    /// SelectionOverlayWindow.ApplyLayout 的 -2/+4 DIP 语义对齐），内部换算为物理像素。
    /// setDipSize=false 用于 SizeToContent 窗口：只写 DIP Left/Top，不固定 Width/Height，
    /// 物理尺寸由 SetWindowPos 提供初值后仍交由内容自适应。
    /// 返回物理 SetWindowPos 是否成功；失败时 DIP 属性路径仍生效（退化行为）。
    /// </summary>
    public static bool SetPhysicalBounds(
        Window window,
        PhysicalRect physicalBounds,
        uint dpiX,
        uint dpiY,
        double padDip = 0,
        bool topmost = true,
        bool setDipSize = true)
    {
        ArgumentNullException.ThrowIfNull(window);

        // 1) DIP 属性先行：窗口 DPI 与目标屏一致时该换算自洽；
        //    不一致时（首次跨 DPI 定位）作为中间值，随后被物理写入覆盖
        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(physicalBounds, dpiX, dpiY);
        window.Left = l - padDip;
        window.Top = t - padDip;
        if (setDipSize)
        {
            window.Width = w + 2 * padDip;
            window.Height = h + 2 * padDip;
        }

        // 2) 确保 HWND 存在（EnsureHandle 只创建不显示）
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
        {
            hwnd = helper.EnsureHandle();
        }
        if (hwnd == IntPtr.Zero) return false;

        // 3) 物理像素定位（最终权威）：padDip 按 x/y DPI 分别换算为物理像素
        int padPxX = (int)Math.Round(padDip * (dpiX == 0 ? 96.0 : dpiX) / 96.0, MidpointRounding.AwayFromZero);
        int padPxY = (int)Math.Round(padDip * (dpiY == 0 ? 96.0 : dpiY) / 96.0, MidpointRounding.AwayFromZero);

        IntPtr insertAfter = topmost ? User32.HWND_TOPMOST : IntPtr.Zero;
        uint flags = User32.SWP_NOACTIVATE | (topmost ? 0 : User32.SWP_NOZORDER);

        return User32.SetWindowPos(
            hwnd,
            insertAfter,
            physicalBounds.X - padPxX,
            physicalBounds.Y - padPxY,
            physicalBounds.Width + 2 * padPxX,
            physicalBounds.Height + 2 * padPxY,
            flags);
    }
}
