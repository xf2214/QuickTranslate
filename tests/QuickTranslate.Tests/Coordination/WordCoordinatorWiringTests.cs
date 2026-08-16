using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

/// <summary>
/// 回归测试：验证 WordInteractionCoordinator 一旦被构造就会订阅 broker.HotkeyFired(Word)。
/// 历史根因：Word 协调器在生产环境只被 DI 注册、从未实例化，导致 HotkeyFired(Word) 无订阅者，
/// Alt+1 热键“按了没反应 / 保存成功但热键不生效”。
/// </summary>
public class WordCoordinatorWiringTests
{
    [Fact]
    public async Task ConstructingCoordinator_SubscribesWordHotkey_EntersCapturing()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out var appLifecycle,
            out var broker,
            out var cursor,
            out var monitors,
            out var capture,
            out var ocr,
            out var selector,
            out var overlay,
            out var translator,
            out var popup);

        // 触发 Word 热键事件，验证协调器确实订阅了 broker.HotkeyFired
        broker.RaiseHotkeyFired(HotkeyEventType.Word);

        // 流水线应进入 Capturing 状态，证明 Word 热键链路被激活
        await CoordinatorTestHelpers.WaitForState(coord, AppState.Capturing);
        Assert.Equal(AppState.Capturing, coord.State);

        // 清理异步流水线，避免遗留任务
        coord.CancelAll(returnIdle: true);
    }

    [Fact]
    public void ConstructingCoordinator_DoesNotHandleBlockHotkey_Duplicate()
    {
        // Word 协调器的 HandleHotkey 对 Block 类型留空（由 BlockInteractionCoordinator 独立处理），
        // 这里验证构造 Word 协调器后，Block 事件不会触发 Word 流水线（状态保持 Idle）。
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out var appLifecycle,
            out var broker,
            out var cursor,
            out var monitors,
            out var capture,
            out var ocr,
            out var selector,
            out var overlay,
            out var translator,
            out var popup);

        broker.RaiseHotkeyFired(HotkeyEventType.Block);

        Assert.Equal(AppState.Idle, coord.State);
    }
}