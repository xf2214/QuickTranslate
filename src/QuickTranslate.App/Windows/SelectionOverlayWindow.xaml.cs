using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
    public bool IsPreviewMode { get; private set; }

    public SelectionOverlayWindow()
    {
        InitializeComponent();
        // 每次窗口变为可见时播放进场动效（只动画边框元素，绝不动窗口 Opacity，避免隐形）
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>切换预览（虚线截图范围框）/ 选定（实线词框）样式。</summary>
    public void SetPreviewMode(bool preview)
    {
        if (IsPreviewMode == preview) return;
        IsPreviewMode = preview;
        PreviewBorder.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        SelectionBorder.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        if (IsPreviewMode)
        {
            BeginPreviewEntryAnimation();
        }
        else
        {
            BeginEntryAnimation();
        }
    }

    private void BeginEntryAnimation()
    {
        // 注意：不要对窗口 Opacity 做“从 0 淡入”——若动画未及时推进，窗口会停在
        // Opacity=0 导致选定框完全不可见（历史 Bug）。这里只对描边做 120ms 收敛：
        // 描边透明度 0.4→1 + 线宽 3→2，不缩窗口、不做位移，避免越界裁剪。
        this.Opacity = 1;

        var brush = SelectionBorder.BorderBrush as SolidColorBrush;
        if (brush != null)
        {
            brush.BeginAnimation(
                SolidColorBrush.OpacityProperty,
                new DoubleAnimation(0.4, 1.0, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        var thickness = new ThicknessAnimation(
            new Thickness(3), new Thickness(2), TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        thickness.Completed += (_, _) =>
        {
            SelectionBorder.BeginAnimation(Border.BorderThicknessProperty, null);
            SelectionBorder.BorderThickness = new Thickness(2);
        };
        SelectionBorder.BeginAnimation(Border.BorderThicknessProperty, thickness);
    }

    private void BeginPreviewEntryAnimation()
    {
        // 预览框首次出现 100ms 淡入；先兜底 Opacity=1，再加 200ms 保护定时器：
        // 若动画因窗口被立即隐藏等原因未推进，强制复位到可见终态。
        this.Opacity = 1;
        PreviewBorder.BeginAnimation(UIElement.OpacityProperty, null);
        PreviewBorder.Opacity = 1;

        PreviewBorder.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            PreviewBorder.BeginAnimation(UIElement.OpacityProperty, null);
            PreviewBorder.Opacity = 1;
        };
        guard.Start();
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
