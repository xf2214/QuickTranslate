using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;

namespace QuickTranslate.App.Windows;

public partial class StatusIndicatorWindow : Window, IPhysicalPlacementAware
{
    public PhysicalRect? LastPhysicalBox { get; set; }
    public uint LastDpiX { get; set; } = 96;
    public uint LastDpiY { get; set; } = 96;
    public PhysicalRect? LastMonitorBounds { get; set; }
    public double? LastMonitorDipLeft { get; set; }
    public double? LastMonitorDipTop { get; set; }

    public void SetPhysicalPlacement(
        PhysicalRect physicalBounds,
        uint dpiX,
        uint dpiY,
        PhysicalRect? monitorPhysicalBounds = null,
        double? monitorDipLeft = null,
        double? monitorDipTop = null)
    {
        LastPhysicalBox = physicalBounds;
        LastDpiX = dpiX;
        LastDpiY = dpiY;
        LastMonitorBounds = monitorPhysicalBounds;
        LastMonitorDipLeft = monitorDipLeft;
        LastMonitorDipTop = monitorDipTop;
    }

    public StatusIndicatorWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        // 置顶 + 不抢焦点 + 点击穿透：纯展示浮层，绝不干扰用户输入
        WindowStyleHelper.SetTopMostNoActivateClickThrough(hwnd);
        this.DpiChanged += OnDpiChanged;
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (!LastPhysicalBox.HasValue) return;

        uint newDpiX = (uint)Math.Round(e.NewDpi.PixelsPerInchX);
        uint newDpiY = (uint)Math.Round(e.NewDpi.PixelsPerInchY);

        LastDpiX = newDpiX;
        LastDpiY = newDpiY;

        WindowPhysicalPlacement.SetPhysicalBounds(
            this,
            LastPhysicalBox.Value,
            newDpiX,
            newDpiY,
            padDip: 0,
            topmost: true,
            setDipSize: false,
            monitorPhysicalBounds: LastMonitorBounds,
            monitorDipLeft: LastMonitorDipLeft,
            monitorDipTop: LastMonitorDipTop);
    }

    public void SetMessage(string message)
    {
        MessageText.Text = message ?? string.Empty;
    }

    /// <summary>每次显示时启动三点跳动循环动画（隐藏时动画随窗口停用，不耗 CPU）。</summary>
    public void StartDotsAnimation()
    {
        const double baseOpacity = 0.25;
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (int i = 0; i < dots.Length; i++)
        {
            var anim = new DoubleAnimation
            {
                From = baseOpacity,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(280),
                BeginTime = TimeSpan.FromMilliseconds(i * 140),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            dots[i].BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    public void StopDotsAnimation()
    {
        Dot1.BeginAnimation(UIElement.OpacityProperty, null);
        Dot2.BeginAnimation(UIElement.OpacityProperty, null);
        Dot3.BeginAnimation(UIElement.OpacityProperty, null);
    }
}