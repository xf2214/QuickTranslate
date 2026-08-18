using System.Windows;
using System.Windows.Threading;

namespace QuickTranslate.App.Windows;

/// <summary>
/// 翻译 Popup 自动隐藏控制器：显示后超过设定时长自动收起；
/// 用户在 Popup 内按下鼠标（复制/交互）即取消倒计时转为常驻，避免打断操作。
/// </summary>
internal static class PopupAutoHide
{
    public static void Attach(Window window, TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
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
        };

        window.IsVisibleChanged += (_, _) =>
        {
            // 每次显示重新计时；隐藏时停止，避免后台空转
            if (window.IsVisible)
            {
                timer.Stop();
                timer.Start();
            }
            else
            {
                timer.Stop();
            }
        };

        // 用户与 Popup 交互（点击复制/关闭/朗读等）→ 视为主动使用，取消自动隐藏
        window.PreviewMouseDown += (_, _) => timer.Stop();
    }
}
