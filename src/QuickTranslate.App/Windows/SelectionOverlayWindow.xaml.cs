using System.Windows;
using System.Windows.Interop;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;

namespace QuickTranslate.App.Windows;

public partial class SelectionOverlayWindow : Window
{
    public DipRect LastLayoutRect { get; private set; }

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        WindowStyleHelper.SetNoActivateToolTopMost(hwnd);
    }
}
