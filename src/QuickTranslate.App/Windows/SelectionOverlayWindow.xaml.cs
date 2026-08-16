using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
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
        // 每次窗口变为可见时播放“边框脉冲”动画，让翻译范围清晰可见（不做窗口淡入，避免隐形）
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            BeginEntryAnimation();
        }
    }

    private void BeginEntryAnimation()
    {
        // 注意：不要对窗口 Opacity 做“从 0 淡入”——若动画未及时推进，窗口会停在
        // Opacity=0 导致选定框完全不可见（历史 Bug）。这里只对边框做不隐藏自身的呼吸，
        // 让框从出现起就清晰可见。
        this.Opacity = 1;
        SelectionBorder.BeginAnimation(
            Border.OpacityProperty,
            new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(300))
            {
                AutoReverse = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
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
