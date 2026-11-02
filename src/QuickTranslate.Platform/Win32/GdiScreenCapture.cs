using System.Drawing;
using System.Drawing.Imaging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.UnmanagedMethods;

namespace QuickTranslate.Platform.Win32;

public sealed class GdiScreenCapture : IScreenCapture
{
    private readonly IMonitorService _monitorService;
    private readonly ICursorService? _cursorService;

    public GdiScreenCapture(IMonitorService monitorService, ICursorService? cursorService = null)
    {
        _monitorService = monitorService;
        _cursorService = cursorService;
    }

    public Task<ScreenFrame> CaptureAroundAsync(PhysicalPoint anchor, PhysicalSize size, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var monitors = _monitorService.EnumerateMonitors();
        var monitor = _monitorService.TryGetMonitorFromPoint(anchor);

        if (monitor == null)
        {
            // anchor 点落入虚拟桌面死角时，优先用粗估矩形与各监视器的最大重叠进行匹配，避免错误退回主屏
            var roughRegion = new PhysicalRect(anchor.X - size.Width / 2, anchor.Y - size.Height / 2, size.Width, size.Height);
            monitor = FindBestOverlappingMonitor(roughRegion, monitors)
                      ?? _monitorService.TryGetPrimary()
                      ?? monitors.FirstOrDefault();
        }

        if (monitor == null)
            throw new InvalidOperationException("No monitors available.");

        // 对截屏物理尺寸做屏幕边界 clamp，防止 4K 200% DPI 下申请 2400x2400 等超大位图
        int maxW = Math.Max(1, monitor.Bounds.Width);
        int maxH = Math.Max(1, monitor.Bounds.Height);
        int clampedW = Math.Clamp(size.Width, 1, maxW);
        int clampedH = Math.Clamp(size.Height, 1, maxH);

        int x = anchor.X - clampedW / 2;
        int y = anchor.Y - clampedH / 2;
        var region = new PhysicalRect(x, y, clampedW, clampedH);
        region = ClampRegionToBounds(region, monitor.Bounds);

        return Task.Run(() => CaptureGdiInternal(region, monitor, ct), ct);
    }

    public Task<ScreenFrame> CaptureRectAsync(PhysicalRect region, MonitorId? monitorHint = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var monitors = _monitorService.EnumerateMonitors();
        // 优先采用与各显示器重叠面积最大的显示器，避免落入虚拟桌面死角时错误 fallback 到主屏截错屏
        var monitor = FindBestOverlappingMonitor(region, monitors);

        if (monitor == null)
        {
            if (monitorHint.HasValue && !monitorHint.Value.IsEmpty)
            {
                foreach (var m in monitors)
                {
                    if (m.Id == monitorHint.Value)
                    {
                        monitor = m;
                        break;
                    }
                }
            }

            if (monitor == null)
            {
                var center = new PhysicalPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
                monitor = _monitorService.TryGetMonitorFromPoint(center)
                          ?? _monitorService.TryGetPrimary()
                          ?? monitors.FirstOrDefault();
            }
        }

        if (monitor == null)
            throw new InvalidOperationException("No monitors available.");

        region = ClampRegionToBounds(region, monitor.Bounds);

        return Task.Run(() => CaptureGdiInternal(region, monitor, ct), ct);
    }

    public static MonitorInfo? FindBestOverlappingMonitor(PhysicalRect region, IEnumerable<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        long maxOverlap = 0;

        foreach (var m in monitors)
        {
            long overlap = ComputeOverlapArea(region, m.Bounds);
            if (overlap > maxOverlap)
            {
                maxOverlap = overlap;
                best = m;
            }
        }

        return maxOverlap > 0 ? best : null;
    }

    public static long ComputeOverlapArea(PhysicalRect r1, PhysicalRect r2)
    {
        int left = Math.Max(r1.X, r2.X);
        int top = Math.Max(r1.Y, r2.Y);
        int right = Math.Min(r1.Right, r2.Right);
        int bottom = Math.Min(r1.Bottom, r2.Bottom);

        if (right <= left || bottom <= top)
            return 0;

        return (long)(right - left) * (bottom - top);
    }

    public static PhysicalRect ClampRegionToBounds(PhysicalRect region, PhysicalRect bounds)
    {
        int finalWidth = Math.Min(region.Width, bounds.Width);
        int finalHeight = Math.Min(region.Height, bounds.Height);

        int left = region.X;
        int top = region.Y;

        if (region.Width > bounds.Width)
        {
            left = bounds.X;
        }
        else
        {
            if (left < bounds.X)
                left = bounds.X;
            else if (left + finalWidth > bounds.Right)
                left = bounds.Right - finalWidth;
        }

        if (region.Height > bounds.Height)
        {
            top = bounds.Y;
        }
        else
        {
            if (top < bounds.Y)
                top = bounds.Y;
            else if (top + finalHeight > bounds.Bottom)
                top = bounds.Bottom - finalHeight;
        }

        return new PhysicalRect(left, top, finalWidth, finalHeight);
    }

    private ScreenFrame CaptureGdiInternal(PhysicalRect region, MonitorInfo monitor, CancellationToken ct = default)
    {
        if (region.IsEmpty)
            throw new ArgumentException("Capture region is empty.", nameof(region));

        IntPtr hDesktop = User32.GetDesktopWindow();
        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            hdcScreen = User32.GetDC(hDesktop);
            if (hdcScreen == IntPtr.Zero)
                throw new InvalidOperationException("Failed to get screen DC.");

            hdcMem = Gdi32.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create memory DC.");

            hBitmap = Gdi32.CreateCompatibleBitmap(hdcScreen, region.Width, region.Height);
            if (hBitmap == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create compatible bitmap.");

            oldBitmap = Gdi32.SelectObject(hdcMem, hBitmap);

            bool bltOk = Gdi32.BitBlt(hdcMem, 0, 0, region.Width, region.Height, hdcScreen, region.X, region.Y, Gdi32.SRCCOPY);

            if (!bltOk)
            {
                bool printOk = User32.PrintWindow(hDesktop, hdcMem, 0);
                if (!printOk)
                    throw new InvalidOperationException("Both BitBlt and PrintWindow failed.");
            }

            Gdi32.SelectObject(hdcMem, oldBitmap);
            oldBitmap = IntPtr.Zero;

            ct.ThrowIfCancellationRequested();

            // FromHbitmap 对桌面 DDB 通常返回 Format32bppRgb，而 ScreenFrame 契约
            // 强制 Format32bppArgb，因此只有已是 Argb 时才能直接包装；否则必须转换
            // （DrawImage 会把无 alpha 的源视为不透明，alpha 归一为 255，与历史行为一致）。
            var captured = Bitmap.FromHbitmap(hBitmap);
            Bitmap bitmap;
            if (captured.PixelFormat == PixelFormat.Format32bppArgb)
            {
                bitmap = captured;
            }
            else
            {
                using (captured)
                {
                    bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(bitmap);
                    g.DrawImage(captured, 0, 0, region.Width, region.Height);
                }
            }

            return new ScreenFrame(bitmap, region, monitor.Id);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && hdcMem != IntPtr.Zero)
                Gdi32.SelectObject(hdcMem, oldBitmap);

            if (hBitmap != IntPtr.Zero)
                Gdi32.DeleteObject(hBitmap);

            if (hdcMem != IntPtr.Zero)
                Gdi32.DeleteDC(hdcMem);

            if (hdcScreen != IntPtr.Zero)
                User32.ReleaseDC(hDesktop, hdcScreen);
        }
    }
}
