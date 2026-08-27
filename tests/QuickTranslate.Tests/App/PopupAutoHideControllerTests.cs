using QuickTranslate.App.Windows;
using Xunit;

namespace QuickTranslate.Tests.App;

/// <summary>
/// PopupAutoHide 状态机单元测试：围绕计时器 Start/Stop 录制，验证悬停暂停语义。
/// WHY：卡片是 WS_EX_NOACTIVATE 无焦点窗口，用户阅读时需悬停暂停 5s 倒计时，移开后再计时；点击后常驻。
/// </summary>
public class PopupAutoHideControllerTests
{
    private sealed class RecordingTimer : IPopupAutoHideTimer
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public void Start() => StartCount++;
        public void Stop() => StopCount++;
        public void Reset() { StartCount = 0; StopCount = 0; }
    }

    [Fact]
    public void OnShown_ArmsTimer()
    {
        var timer = new RecordingTimer();
        var sut = new PopupAutoHideController(timer);

        sut.OnShown();

        // 显示应先 Stop 再 Start，确保重武装完整 delay
        Assert.Equal(1, timer.StartCount);
        Assert.Equal(1, timer.StopCount);
    }

    [Fact]
    public void OnMouseEnter_PausesTimer()
    {
        var timer = new RecordingTimer();
        var sut = new PopupAutoHideController(timer);
        sut.OnShown();
        timer.Reset();

        sut.OnMouseEnter();

        // 悬停应暂停：触发一次 Stop，不再 Start
        Assert.Equal(1, timer.StopCount);
        Assert.Equal(0, timer.StartCount);
    }

    [Fact]
    public void OnMouseLeave_ResumesFullDelay_WhenVisibleAndNotInteracted()
    {
        var timer = new RecordingTimer();
        var sut = new PopupAutoHideController(timer);
        sut.OnShown();
        sut.OnMouseEnter();
        timer.Reset();

        sut.OnMouseLeave();

        // 移开且未交互、仍可见：应 Stop + Start 重武装完整 delay
        Assert.Equal(1, timer.StopCount);
        Assert.Equal(1, timer.StartCount);
    }

    [Fact]
    public void OnUserInteraction_CancelsAndLeaveAfterClick_DoesNotResume()
    {
        var timer = new RecordingTimer();
        var sut = new PopupAutoHideController(timer);
        sut.OnShown();
        timer.Reset();

        sut.OnUserInteraction();

        Assert.Equal(1, timer.StopCount);
        Assert.Equal(0, timer.StartCount);

        timer.Reset();
        // 点击后常驻：后续 MouseLeave 必须 NOT resurrect timer
        sut.OnMouseLeave();

        Assert.Equal(0, timer.StartCount);
        Assert.Equal(0, timer.StopCount);
    }

    [Fact]
    public void OnHidden_CancelsAndClearsInteracted()
    {
        var timer = new RecordingTimer();
        var sut = new PopupAutoHideController(timer);
        sut.OnShown();
        sut.OnUserInteraction();
        timer.Reset();

        sut.OnHidden();

        // 隐藏应停表（即使已停也再 Stop 一次以确保取消），并清除 interacted
        Assert.Equal(1, timer.StopCount);
        Assert.False(sut.IsInteracted);
        Assert.False(sut.IsVisible);
    }

    [Fact]
    public void SecondShow_AfterInteraction_RearmsNormally()
    {
        var timer = new RecordingTimer();
        var sut = new PopupAutoHideController(timer);
        sut.OnShown();
        sut.OnUserInteraction();
        sut.OnHidden();
        timer.Reset();

        sut.OnShown();

        // 复用窗口再次显示：应重置 interacted 并正常武装
        Assert.False(sut.IsInteracted);
        Assert.True(sut.IsVisible);
        Assert.Equal(1, timer.StartCount);
        Assert.Equal(1, timer.StopCount);

        timer.Reset();
        sut.OnMouseEnter();
        Assert.Equal(1, timer.StopCount);

        timer.Reset();
        sut.OnMouseLeave();
        Assert.Equal(1, timer.StartCount);
    }
}
