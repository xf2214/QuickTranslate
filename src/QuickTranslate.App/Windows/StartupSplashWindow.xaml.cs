using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickTranslate.App.ViewModels;

namespace QuickTranslate.App.Windows;

public partial class StartupSplashWindow : Window
{
    private readonly StartupSplashViewModel _vm;
    private readonly DispatcherTimer _fallbackTimer;
    private bool _closing;
    private bool _staggerPlayed;

    public StartupSplashViewModel ViewModel => _vm;

    public StartupSplashWindow() : this(new StartupSplashViewModel())
    {
    }

    public StartupSplashWindow(StartupSplashViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = _vm;
        InitializeComponent();

        Loaded += OnLoaded;
        EnterButton.Click += (_, _) => CloseWithAnimation();
        PreviewKeyDown += OnPreviewKeyDown;

        // 6 秒兜底自动关闭
        _fallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _fallbackTimer.Tick += (_, _) =>
        {
            _fallbackTimer.Stop();
            if (!_closing) CloseWithAnimation();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 只动画 RootBorder，绝不动画 Window.Opacity
        PopupTransition.PlayEntry(RootBorder);
        _fallbackTimer.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PlayStagger));
    }

    private void PlayStagger()
    {
        if (_staggerPlayed) return;
        _staggerPlayed = true;

        // 每项 Opacity 0->1 120ms BeginTime i*70ms + TranslateY 8->0
        for (int i = 0; i < CheckList.Items.Count; i++)
        {
            if (CheckList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                // 若已生成容器则直接动画；未生成则对可视子树中的对应 Border 做动画
                var target = FindCardBorder(container) ?? container;
                AnimateItem(target, i);
            }
        }

        // 虚拟化/未生成容器的兜底：延迟一帧后重试未动画的项
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // 遍历可视树中的所有卡片 Border 做 stagger（更可靠）
            var borders = FindVisualChildren<System.Windows.Controls.Border>(CheckList);
            int idx = 0;
            foreach (var b in borders)
            {
                // 仅对 CornerRadius=10 的卡片 Border 生效
                if (b.CornerRadius.TopLeft != 10) continue;
                // 已有动画的不重复
                if (b.Opacity == 0) continue;
                // 若已有 RenderTransform 则跳过已动画的
                // 初次 stagger：统一重置再动画
                b.Opacity = 0;
                b.RenderTransform = new TranslateTransform(0, 8);
                AnimateItem(b, idx);
                idx++;
            }
        }));
    }

    private static void AnimateItem(FrameworkElement el, int index)
    {
        el.Opacity = 0;
        var translate = new TranslateTransform(0, 8);
        el.RenderTransform = translate;

        var delay = TimeSpan.FromMilliseconds(index * 70);
        var duration = TimeSpan.FromMilliseconds(120);

        var fade = new DoubleAnimation(0, 1, duration)
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(8, 0, duration)
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        slide.Completed += (_, _) => el.RenderTransform = null;

        el.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private static System.Windows.Controls.Border? FindCardBorder(FrameworkElement container)
    {
        // ItemContainer 通常是 ContentPresenter，直接在子树中找第一个 CornerRadius=10 的 Border
        foreach (var b in FindVisualChildren<System.Windows.Controls.Border>(container))
            if (b.CornerRadius.TopLeft == 10) return b;
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithAnimation();
        }
    }

    /// <summary>供外部更新单项检查状态（线程安全）。</summary>
    public void UpdateCheck(int index, StartupCheckStatus status, string? detail = null)
        => _vm.UpdateItem(index, status, detail);

    public void UpdateCheck(string title, StartupCheckStatus status, string? detail = null)
        => _vm.UpdateItem(title, status, detail);

    public void SetProgress(double value) => _vm.SetProgress(value);

    /// <summary>
    /// 带退场动效的关闭：900ms 后 PlayExit。重复调用只生效一次。
    /// </summary>
    public void CloseWithAnimation()
    {
        if (_closing) return;
        _closing = true;
        _fallbackTimer.Stop();

        // 若已完成全部检查，温和停留 900ms 再退场；否则立即退场（Esc/兜底场景）
        bool allDone = IsAllDone();
        var delay = allDone ? TimeSpan.FromMilliseconds(900) : TimeSpan.Zero;

        if (delay == TimeSpan.Zero)
        {
            PopupTransition.PlayExit(this, RootBorder);
        }
        else
        {
            var t = new DispatcherTimer { Interval = delay };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (IsVisible) PopupTransition.PlayExit(this, RootBorder);
                else Hide();
            };
            t.Start();
        }
    }

    /// <summary>标记全部完成并自动在 900ms 后退场。</summary>
    public void MarkCompletedAndClose()
    {
        _vm.SetProgress(1);
        CloseWithAnimation();
    }

    private bool IsAllDone()
    {
        foreach (var it in _vm.Items)
            if (it.Status is StartupCheckStatus.Pending or StartupCheckStatus.Checking)
                return false;
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _fallbackTimer.Stop();
        base.OnClosed(e);
    }
}
