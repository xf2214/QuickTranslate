using System.Drawing;
using System.Drawing.Imaging;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Capture;

public record class ScreenFrame : IDisposable
{
    public Bitmap Bitmap { get; }
    public PhysicalRect Region { get; }
    public MonitorId MonitorId { get; }

    private bool _disposed;

    public ScreenFrame(Bitmap bitmap, PhysicalRect region, MonitorId monitorId)
    {
        if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
            throw new ArgumentException("Bitmap must be Format32bppArgb", nameof(bitmap));

        Bitmap = bitmap;
        Region = region;
        MonitorId = monitorId;
    }

    void IDisposable.Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Bitmap?.Dispose();
    }
}
