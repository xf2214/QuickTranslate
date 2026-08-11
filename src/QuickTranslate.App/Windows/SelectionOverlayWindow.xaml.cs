using System.Windows;
using System.Windows.Interop;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;
using QuickTranslate.App.Coordination;

namespace QuickTranslate.App.Windows;

public partial class SelectionOverlayWindow : Window
{
    public DipRect LastLayoutRect { get; private set; }
    public PhysicalRect? LastPhysicalBox { get; private set; }
    public uint LastDpiX { get; private set; } = 96;
    public uint LastDpiY { get; private set; } = 96;

    public SelectionOverlayWindow()
    {
        InitializeComponent();
    }

    public void ApplyLayout(DipRect dipRect)
    {
        LastLayoutRect = dipRect;

        this.Left = dipRect.X - 2;
        this.Top = dipRect.Y - 2;
        this.Width = dipRect.Width + 4;
        this.Height = dipRect.Height + 4;

        SelectionBorder.Width = dipRect.Width;
        SelectionBorder.Height = dipRect.Height;
        SelectionBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        SelectionBorder.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
    }

    public void ApplyPhysicalLayout(PhysicalRect physicalBox, uint dpiX, uint dpiY)
    {
        LastPhysicalBox = physicalBox;
        LastDpiX = dpiX;
        LastDpiY = dpiY;

        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(physicalBox, dpiX, dpiY);
        ApplyLayout(new DipRect(l, t, w, h));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        WindowStyleHelper.SetNoActivateToolTopMost(hwnd);

        this.DpiChanged += OnDpiChanged;
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (!LastPhysicalBox.HasValue) return;

        uint newDpiX = (uint)Math.Round(e.NewDpi.PixelsPerInchX);
        uint newDpiY = (uint)Math.Round(e.NewDpi.PixelsPerInchY);

        LastDpiX = newDpiX;
        LastDpiY = newDpiY;

        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(LastPhysicalBox.Value, newDpiX, newDpiY);
        ApplyLayout(new DipRect(l, t, w, h));
    }
}
