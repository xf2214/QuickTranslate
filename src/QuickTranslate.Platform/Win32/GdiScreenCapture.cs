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

        var monitor = _monitorService.TryGetMonitorFromPoint(anchor)
                      ?? _monitorService.EnumerateMonitors().FirstOrDefault()
                      ?? _monitorService.TryGetPrimary();

        if (monitor == null)
            throw new InvalidOperationException("No monitors available.");

        int x = anchor.X - size.Width / 2;
        int y = anchor.Y - size.Height / 2;
        var region = new PhysicalRect(x, y, size.Width, size.Height);
        region = ClampRegionToBounds(region, monitor.Bounds);

        return Task.Run(() => CaptureGdiInternal(region, monitor, ct), ct);
    }

    public Task<ScreenFrame> CaptureRectAsync(PhysicalRect region, MonitorId? monitorHint = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        MonitorInfo? monitor = null;
        if (monitorHint.HasValue && !monitorHint.Value.IsEmpty)
        {
            var center = new PhysicalPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
            monitor = _monitorService.TryGetMonitorFromPoint(center);
            if (monitor == null || monitor.Id != monitorHint.Value)
            {
                foreach (var m in _monitorService.EnumerateMonitors())
                {
                    if (m.Id == monitorHint.Value)
                    {
                        monitor = m;
                        break;
                    }
                }
            }
        }

        if (monitor == null)
        {
            var center = new PhysicalPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
            monitor = _monitorService.TryGetMonitorFromPoint(center)
                      ?? _monitorService.EnumerateMonitors().FirstOrDefault()
                      ?? _monitorService.TryGetPrimary();
        }

        if (monitor == null)
            throw new InvalidOperationException("No monitors available.");

        region = ClampRegionToBounds(region, monitor.Bounds);

        return Task.Run(() => CaptureGdiInternal(region, monitor, ct), ct);
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
