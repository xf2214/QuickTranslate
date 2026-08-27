using System.Windows;

namespace QuickTranslate.App.Windows;

/// <summary>
/// 翻译 Popup 自动隐藏粘合层：保持 Attach(Window, TimeSpan) 签名不变，内部委托给
/// PopupAutoHideController 状态机；悬停时暂停、移开后重武装、点击后常驻。
/// WHY：原逻辑用 DispatcherTimer 直接驱动，无法单元测试悬停状态机；抽离控制器后
/// Attach 仅做事件订阅，计时器走 IPopupAutoHideTimer 抽象（生产为 DispatcherTimer）。
/// </summary>
internal static class PopupAutoHide
{
    public static void Attach(Window window, TimeSpan delay)
    {
        // 生产计时器：Tick 负责到期隐藏（淡出优先），与状态机解耦
        DispatcherPopupAutoHideTimer? adapter = null;
        adapter = new DispatcherPopupAutoHideTimer(delay, (_, _) =>
        {
            // 隐藏时停表由控制器 OnHidden 处理，但 Tick 自身先 Stop 避免重入
            adapter!.Stop();
            if (window.IsVisible)
            {
                // 优先走淡出退场（苹果风格动效），未实现的窗口回退为立即隐藏
                if (window is IFadeOutHideable fadeOut)
                {
                    fadeOut.HideWithFade();
                }
                else
                {
                    window.Hide();
                }
            }
        });

        var controller = new PopupAutoHideController(adapter);

        window.IsVisibleChanged += (_, _) =>
        {
            // 每次显示重新计时；隐藏时停止并重置交互标记
            if (window.IsVisible)
            {
                controller.OnShown();
            }
            else
            {
                controller.OnHidden();
            }
        };

        // 悬停暂停：MouseEnter 停表，MouseLeave 若仍可见且未交互则重武装完整 delay
        // WHY：卡片是 WS_EX_NOACTIVATE 无焦点窗口，WPF 的 MouseEnter/Leave 仍可在未激活时触发
        window.MouseEnter += (_, _) => controller.OnMouseEnter();
        window.MouseLeave += (_, _) => controller.OnMouseLeave();

        // 用户与 Popup 交互（点击复制/关闭/朗读等）→ 视为主动使用，取消自动隐藏转为常驻
        window.PreviewMouseDown += (_, _) => controller.OnUserInteraction();
    }
}
