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

    /// <summary>调试模式：true 时选定区域显示为实线框；false 时显示扫描式动画。</summary>
    public bool IsDebugBoxMode { get; private set; }

    public SelectionOverlayWindow()
    {
        InitializeComponent();
        // 每次窗口变为可见时播放进场动效（只动画边框元素，绝不动窗口 Opacity，避免隐形）
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>切换预览（虚线截图范围框）/ 选定（扫描动画或调试框）样式。</summary>
    public void SetPreviewMode(bool preview)
    {
        if (IsPreviewMode == preview) return;
        IsPreviewMode = preview;
        ApplyElementVisibility();
    }

    /// <summary>切换调试模式：开启后选定区域回退为实线框，便于排查选词/OCR 定位。</summary>
    public void SetDebugBoxMode(bool debug)
    {
        if (IsDebugBoxMode == debug) return;
        IsDebugBoxMode = debug;
        ApplyElementVisibility();
    }

    private void ApplyElementVisibility()
    {
        PreviewBorder.Visibility = IsPreviewMode ? Visibility.Visible : Visibility.Collapsed;
        SelectionBorder.Visibility = (!IsPreviewMode && IsDebugBoxMode) ? Visibility.Visible : Visibility.Collapsed;
        ScanPanel.Visibility = (!IsPreviewMode && !IsDebugBoxMode) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            // 隐藏时停掉扫描动画，避免后台持续消耗
            ScanTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        if (IsPreviewMode)
        {
            BeginPreviewEntryAnimation();
        }
        else if (IsDebugBoxMode)
        {
            BeginEntryAnimation();
        }
        else
        {
            BeginScanEntryAnimation();
        }
    }

    private void BeginScanEntryAnimation()
    {
        // 零风险优化：窗口 Opacity 先兜底为 1，只对元素做 100ms 淡入，
        // 保护定时器从 200ms 收紧到 100ms（首帧立即可见 + 兜底复位保留）。
        this.Opacity = 1;
        ScanPanel.BeginAnimation(UIElement.OpacityProperty, null);
        ScanPanel.Opacity = 1;

        ScanPanel.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            ScanPanel.BeginAnimation(UIElement.OpacityProperty, null);
            ScanPanel.Opacity = 1;
        };
        guard.Start();

        BeginScanSweep();
    }

    private void BeginScanSweep()
    {
        // 苹果风格：扫描线从左外侧单次扫到右外侧（缓入缓出），扫完即停；
        // 结束后清除动画并把扫描线停靠在右外侧，避免残留在可见区域。
        double regionWidth = ScanPanel.ActualWidth > 0
            ? ScanPanel.ActualWidth
            : Math.Max(0, LastLayoutRect.Width);
        double lineWidth = ScanLine.Width;

        var sweep = new DoubleAnimation(
            -lineWidth,
            regionWidth,
            TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        sweep.Completed += (_, _) =>
        {
            ScanTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ScanTranslate.X = regionWidth;
            ScanLine.CacheMode = null;
        };
        // 扫描线光栅静态：缓存后位移只走合成，300ms 内免渐变重绘；结束即清，保证换尺寸下次重栅
        ScanLine.CacheMode = new BitmapCache();
        ScanTranslate.BeginAnimation(TranslateTransform.XProperty, sweep);
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

        // 窗口已可见时（如预览→选定切换）不会触发 IsVisibleChanged，
        // 在这里补播一次单次扫描，并与新宽度对齐
        if (!IsPreviewMode && !IsDebugBoxMode && IsVisible)
        {
            BeginScanSweep();
        }
    }

    public void ApplyPhysicalLayout(PhysicalRect physicalBox, uint dpiX, uint dpiY)
    {
        LastPhysicalBox = physicalBox;
        LastDpiX = dpiX;
        LastDpiY = dpiY;

        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(physicalBox, dpiX, dpiY);
        // DIP 路径先行（保持 LastLayoutRect/内部元素布局语义），随后 HWND 物理定位覆盖：
        // 窗口创建时刻的 DPI 认知 ≠ 目标屏 DPI 时（高缩放/混合 DPI/启动后改分辨率），
        // DIP 换算会按错误比例缩放且不触发 WM_DPICHANGED，物理写入是唯一确定性定位。
        ApplyLayout(new DipRect(l, t, w, h));
        WindowPhysicalPlacement.SetPhysicalBounds(this, physicalBox, dpiX, dpiY, padDip: 2);
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

        // DIP 重算（窗口 DPI 已更新，换算自洽）+ 物理重定位：
        // WPF 对 WM_DPICHANGED 的默认自动缩放会把窗口物理几何按建议矩形调整，
        // 这里以 LastPhysicalBox 为准做物理覆盖，防止残留错误几何。
        var (l, t, w, h) = PerMonitorDpiHelpers.ToDip(LastPhysicalBox.Value, newDpiX, newDpiY);
        ApplyLayout(new DipRect(l, t, w, h));
        WindowPhysicalPlacement.SetPhysicalBounds(this, LastPhysicalBox.Value, newDpiX, newDpiY, padDip: 2);
    }
}
