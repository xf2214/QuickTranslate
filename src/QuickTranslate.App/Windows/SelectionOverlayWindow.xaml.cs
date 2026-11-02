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
    public DipRect TargetLayoutRect => LastLayoutRect;
    public PhysicalRect? LastPhysicalBox { get; private set; }
    public uint LastDpiX { get; private set; } = 96;
    public uint LastDpiY { get; private set; } = 96;
    public bool IsPreviewMode { get; private set; }
    public bool IsDragging { get; private set; }

    /// <summary>切换拖拽选区状态：拖拽中高亮展示纯净选框，松开确认时才播放扫描光效。</summary>
    public void SetDragState(bool isDragging)
    {
        if (IsDragging == isDragging) return;
        IsDragging = isDragging;
        ApplyElementVisibility();
    }

    /// <summary>调试模式：true 时选定区域显示为实线框；false 时显示扫描式动画。</summary>
    public bool IsDebugBoxMode { get; private set; }

    public SelectionOverlayWindow()
    {
        InitializeComponent();
        ScanLine.Width = 16;
        // 每次窗口变为可见时播放进场动效（只动画边框元素，绝不动窗口 Opacity，避免隐形）
        IsVisibleChanged += OnIsVisibleChanged;
        ScanLineContainer.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                UpdateClipGeometry(e.NewSize.Width, e.NewSize.Height);
            }
        };
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
        if (IsPreviewMode)
        {
            PreviewBorder.Visibility = Visibility.Visible;
            SelectionBorder.Visibility = Visibility.Collapsed;
            ScanPanel.Visibility = Visibility.Collapsed;
        }
        else if (IsDebugBoxMode)
        {
            PreviewBorder.Visibility = Visibility.Collapsed;
            SelectionBorder.Visibility = Visibility.Visible;
            ScanPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewBorder.Visibility = Visibility.Collapsed;
            SelectionBorder.Visibility = Visibility.Collapsed;
            ScanPanel.Visibility = Visibility.Visible;
            ScanLineContainer.Visibility = IsDragging ? Visibility.Collapsed : Visibility.Visible;
        }
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

        BeginScanSweep(TargetLayoutRect.Width);
    }

    private void BeginScanSweep(double? sweepWidth = null)
    {
        // 苹果风格：扫描线从左外侧单次扫到右外侧（缓入缓出），扫完即停；
        // 动画终点严格基于当前权威的 TargetLayoutRect.Width（或 ApplyLayout 传入的宽度），
        // 不得使用可能尚未经过 Measure/Arrange 的旧 ScanPanel.ActualWidth（避免从 740px 截图预览框切换到 45px 选词框时动画飞出屏幕）。
        double targetWidth = sweepWidth ?? TargetLayoutRect.Width;
        double regionWidth = Math.Max(0, targetWidth);
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

    private void UpdateClipGeometry(double width, double height)
    {
        var clipRect = new Rect(0, 0, Math.Max(0, width), Math.Max(0, height));
        if (ScanClipGeometry != null && !ScanClipGeometry.IsFrozen)
        {
            ScanClipGeometry.Rect = clipRect;
        }
        else if (ScanLineContainer != null)
        {
            ScanLineContainer.Clip = new RectangleGeometry(clipRect, 4, 4);
        }
    }

    public void ApplyLayout(DipRect dipRect)
    {
        LastLayoutRect = dipRect;

        this.Left = dipRect.X - 2;
        this.Top = dipRect.Y - 2;
        this.Width = dipRect.Width + 4;
        this.Height = dipRect.Height + 4;

        SelectionBorder.Width = Math.Max(0, dipRect.Width);
        SelectionBorder.Height = Math.Max(0, dipRect.Height);
        SelectionBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        SelectionBorder.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;

        ScanPanel.Width = Math.Max(0, dipRect.Width);
        ScanPanel.Height = Math.Max(0, dipRect.Height);
        ScanPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        ScanPanel.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;

        // 动态根据当前物理选框宽度自适应计算扫描线宽度：
        // ScanLine.Width = Math.Clamp(boxWidth * 0.35, 8.0, 32.0)，短词不再穿模
        double boxWidth = dipRect.Width;
        ScanLine.Width = Math.Clamp(boxWidth * 0.35, 8.0, 32.0);

        UpdateClipGeometry(dipRect.Width, dipRect.Height);

        // 窗口已可见时（如预览→选定切换）不会触发 IsVisibleChanged，
        // 在这里补播一次单次扫描，并与新宽度对齐
        if (!IsPreviewMode && !IsDebugBoxMode && !IsDragging && IsVisible)
        {
            BeginScanSweep(dipRect.Width);
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