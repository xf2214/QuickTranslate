using System.Windows.Threading;

namespace QuickTranslate.App.Windows;

/// <summary>
/// 计时器抽象：为可测试性抽离 DispatcherTimer，生产用 DispatcherTimerAdapter，测试用录制型 Fake。
/// WHY：WPF 计时器依赖 Dispatcher，单元测试无法真实驱动；抽象后控制器只操作 Start/Stop 语义。
/// </summary>
internal interface IPopupAutoHideTimer
{
    void Start();
    void Stop();
}

/// <summary>
/// 生产实现：对 DispatcherTimer 的薄封装。
/// </summary>
internal sealed class DispatcherPopupAutoHideTimer : IPopupAutoHideTimer
{
    private readonly DispatcherTimer _inner;

    public DispatcherPopupAutoHideTimer(TimeSpan delay, EventHandler tick)
    {
        _inner = new DispatcherTimer { Interval = delay };
        _inner.Tick += tick;
    }

    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
}

/// <summary>
/// Popup 自动隐藏状态机控制器（可测试核心）。
/// 状态机：显示→武装计时；鼠标移入→暂停；鼠标移开→若仍可见且未交互则重武装完整 delay；
/// 任意点击交互→置为常驻（interacted=true）并永久停表；隐藏→停表并重置状态以便下次显示干净重来。
/// </summary>
internal sealed class PopupAutoHideController
{
    private readonly IPopupAutoHideTimer _timer;
    private bool _isVisible;
    private bool _isHovered;
    private bool _interacted;

    public PopupAutoHideController(IPopupAutoHideTimer timer)
    {
        _timer = timer;
    }

    // 暴露给测试断言的只读状态（不影响生产行为）
    internal bool IsVisible => _isVisible;
    internal bool IsInteracted => _interacted;
    internal bool IsHovered => _isHovered;

    /// <summary>窗口变为可见：重置交互/悬停标记并重武装完整计时。</summary>
    public void OnShown()
    {
        _isVisible = true;
        _interacted = false;
        _isHovered = false;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>窗口隐藏（含淡出完成后的 Hide）：停表并重置，下次显示干净重来。</summary>
    public void OnHidden()
    {
        // WHY：隐藏（含退出淡出期间的 Hide）必须立即停表，避免后台空转；
        // 同时重置 interacted/hovered，下次复用窗口显示时重新开始完整倒计时。
        _isVisible = false;
        _isHovered = false;
        _interacted = false;
        _timer.Stop();
    }

    /// <summary>鼠标移入卡片：暂停倒计时，便于用户阅读。</summary>
    public void OnMouseEnter()
    {
        // 未可见或已交互（常驻）时不处理，避免误停
        if (!_isVisible || _interacted)
        {
            return;
        }

        _isHovered = true;
        _timer.Stop();
    }

    /// <summary>鼠标移出卡片：若仍可见且未被交互，则重武装完整 delay。</summary>
    public void OnMouseLeave()
    {
        // 已交互常驻或窗口已不可见时，绝不 resurrect 计时器（点击后常驻语义）
        if (!_isVisible || _interacted)
        {
            return;
        }

        _isHovered = false;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>用户点击交互（复制/朗读/关闭等）：转为常驻，永久停表。</summary>
    public void OnUserInteraction()
    {
        // WHY：PreviewMouseDown 视为用户主动使用，取消自动隐藏转为常驻；
        // 后续 MouseLeave 必须不再重武装，由 _interacted 守卫。
        _interacted = true;
        _timer.Stop();
    }
}
