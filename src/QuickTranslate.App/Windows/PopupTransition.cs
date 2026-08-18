using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QuickTranslate.App.Windows;

/// <summary>
/// 支持"淡出退场"的弹窗接口：PopupAutoHide 与弹窗服务统一走此方法隐藏，
/// 未实现该接口的窗口回退为立即 Hide()。
/// </summary>
public interface IFadeOutHideable
{
    void HideWithFade();
}

/// <summary>
/// 苹果风格弹窗进出场动效（共享组件）。
/// 安全铁律（历史 Bug 防线：窗口级 Opacity 动画曾导致选框卡在 0 永久隐形）：
/// 1. 只动画内容元素（RootBorder），绝不动画 Window 自身 Opacity；
/// 2. 进场前同步把 Opacity/缩放复位到可见终态，动画被中断时元素依然可见；
/// 3. 退场完成后立即 Hide() 并复位 Opacity=1，保证下一次显示不会卡在透明态；
/// 4. 所有动画 ≤ 200ms。
/// </summary>
public static class PopupTransition
{
    private sealed class TransitionState
    {
        public bool Exiting;
        public DoubleAnimation? ActiveExit;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, TransitionState> States = new();

    private static TransitionState GetState(FrameworkElement root) => States.GetValue(root, _ => new TransitionState());

    /// <summary>
    /// 进场：轻微缩放 0.97→1 + 淡入，160ms EaseOut，中心为变换原点。
    /// 同时取消任何进行中的退场动画（窗口在退场途中被复用时不残留）。
    /// </summary>
    public static void PlayEntry(FrameworkElement root)
    {
        var state = GetState(root);
        state.Exiting = false;
        state.ActiveExit = null;

        // 兜底：先清除旧动画并复位到可见终态，再启动新动画
        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 1;

        var scale = new ScaleTransform(1, 1);
        root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        root.RenderTransform = scale;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = ease
        };
        var grow = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = ease
        };
        grow.Completed += (_, _) => root.RenderTransform = null;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// 退场：140ms 淡出后 Hide() 并复位 Opacity=1。
    /// 窗口不可见时直接 Hide()；重复调用只生效一次。
    /// </summary>
    public static void PlayExit(Window window, FrameworkElement root)
    {
        if (!window.IsVisible)
        {
            window.Hide();
            return;
        }

        var state = GetState(root);
        if (state.Exiting) return;
        state.Exiting = true;

        var fade = new DoubleAnimation(root.Opacity, 0.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        state.ActiveExit = fade;

        fade.Completed += (_, _) =>
        {
            // 竞态防护：若退场期间窗口被重新显示（PlayEntry 已接管），不做隐藏
            if (!state.Exiting || !ReferenceEquals(state.ActiveExit, fade))
            {
                return;
            }

            state.Exiting = false;
            state.ActiveExit = null;

            window.Hide();

            // 复位到可见终态：下次显示不会卡在 Opacity=0
            root.BeginAnimation(UIElement.OpacityProperty, null);
            root.Opacity = 1;
            root.RenderTransform = null;
        };

        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
