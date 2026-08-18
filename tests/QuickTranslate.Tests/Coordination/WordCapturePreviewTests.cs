using System.Reflection;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

/// <summary>
/// Word 模式新交互：选取前虚线范围框、截图尺寸 15 词×4 行、选定框超时自动消失。
/// </summary>
public class WordCapturePreviewTests
{
    private static readonly MonitorId Mid = new(new IntPtr(1), @"\\.\DISPLAY1");

    private static OcrLayoutResult CreateOcrResult(PhysicalRect region, params OcrLine[] lines)
    {
        return new OcrLayoutResult(
            CaptureRegion: region,
            Lines: lines,
            Timings: new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            CaptureTime: DateTimeOffset.Now,
            DpiX: 96,
            DpiY: 96,
            EngineName: "FakeOcr");
    }

    private static OcrLine MakeLine(int x, int y, int w, int h)
    {
        var words = new[] { new OcrWord(new PhysicalRect(x, y, w, h), "hello", 0.9f, 0) };
        return new OcrLine(new PhysicalRect(x, y, w, h), words, "hello");
    }

    private static void SetAutoHideMs(int ms)
    {
        var field = typeof(WordInteractionCoordinator).GetField("SelectionAutoHideMs",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(null, ms);
    }

    [Fact]
    public async Task PreviewRangeBox_ShownBeforeOcr_SolidSelectionAfter()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup);

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);

        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(400, 300, 720, 320), Mid));
        await Task.Delay(40);

        // OCR 尚未完成：范围预览框（虚线）已出现，选定框尚未出现
        Assert.Equal(1, overlay.PreviewShowCount);
        Assert.Equal(0, overlay.SelectionShowCount);
        Assert.Equal(new PhysicalRect(400, 300, 720, 320), overlay.ShowCalls[^1].Box);
        Assert.True(overlay.ShowCalls[^1].Preview);

        ocr.RecognizeTcs.SetResult(CreateOcrResult(new PhysicalRect(400, 300, 720, 320)));
        await Task.Delay(40);

        // 选词完成：实线选定框出现
        Assert.Equal(1, overlay.SelectionShowCount);
        Assert.False(overlay.ShowCalls[^1].Preview);

        translator.TranslateWordTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
        await Task.Delay(400);

        Assert.Equal(AppState.Displaying, coord.State);
        Assert.Equal(1, popup.ShowCount);
    }

    [Fact]
    public async Task InitialCaptureSize_Is15WordsBy4Lines()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out _,
            out var overlay, out var translator, out var popup);

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);

        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), Mid));
        await Task.Delay(30);

        // 估值行高 20：宽 = 15 × 0.62 × 20 = 186，高 = 4 × 20 = 80
        Assert.NotEmpty(capture.CaptureAroundSizes);
        Assert.Equal(186, capture.CaptureAroundSizes[0].Width);
        Assert.Equal(80, capture.CaptureAroundSizes[0].Height);

        ocr.RecognizeTcs.SetResult(CreateOcrResult(new PhysicalRect(0, 0, 720, 320)));
        translator.TranslateWordTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
        await Task.Delay(400);
    }

    [Fact]
    public async Task EdgeClippedSelection_RetriesWithActualLineHeight()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out var selector,
            out var overlay, out var translator, out var popup);

        int selectCall = 0;
        selector.SelectFunc = (_, _, _) =>
        {
            selectCall++;
            // 第一次：词框贴近截图左边缘（被截断风险）→ 触发重抓
            // 第二次：返回居中词框 → 不再重抓
            var box = selectCall == 1
                ? new PhysicalRect(3, 20, 50, 20)
                : new PhysicalRect(100, 100, 50, 20);
            return new SelectionResult(
                Text: "hello",
                ContextLine: "hello world",
                Box: box,
                Kind: SelectionKind.Word,
                Confidence: 0.95f,
                OperationId: Guid.NewGuid(),
                NoTextFound: false);
        };

        // OCR 结果含光标所在行（行高 30），供重抓尺寸计算
        var frame = new PhysicalRect(0, 0, 186, 80);
        var ocrResult = CreateOcrResult(frame, MakeLine(20, 88, 150, 30));

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);
        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(frame, Mid));
        await Task.Delay(30);
        ocr.RecognizeTcs.SetResult(ocrResult);
        await Task.Delay(60);

        // 第二次截图：横向触边 → 宽翻倍 max(186×2, 15×0.62×30=279) = 372；
        // 纵向未触边但行高重算更大 → 高 = max(80, 4×30=120) = 120（只增不减）
        Assert.Equal(2, capture.CaptureAroundCount);
        Assert.Equal(372, capture.CaptureAroundSizes[1].Width);
        Assert.Equal(120, capture.CaptureAroundSizes[1].Height);

        translator.TranslateWordTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
        await Task.Delay(400);

        Assert.Equal(AppState.Displaying, coord.State);
        Assert.Equal(1, overlay.SelectionShowCount);
    }

    [Fact]
    public async Task SelectionBox_AutoHides_AfterTimeout()
    {
        // 计时从 Popup 出现开始；400ms 足够在读取基线后触发
        SetAutoHideMs(400);
        try
        {
            var coord = CoordinatorTestHelpers.CreateCoordinator(
                out _, out var broker, out _, out _,
                out var capture, out var ocr, out _,
                out var overlay, out var translator, out var popup);

            broker.RaiseHotkeyFired(HotkeyEventType.Word);
            await Task.Delay(30);
            capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
                new PhysicalRect(0, 0, 720, 320), Mid));
            await Task.Delay(30);
            ocr.RecognizeTcs.SetResult(CreateOcrResult(new PhysicalRect(0, 0, 720, 320)));
            await Task.Delay(40);
            translator.TranslateWordTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
            await Task.Delay(350);

            Assert.Equal(AppState.Displaying, coord.State);
            int hidesBefore = overlay.HideAllCount;

            // 超时后选定框自动收起（Popup 保留）
            await Task.Delay(450);
            Assert.True(overlay.HideAllCount > hidesBefore);
            Assert.Equal(1, popup.ShowCount);
        }
        finally
        {
            SetAutoHideMs(5000);
        }
    }

    [Fact]
    public async Task CancelAll_BeforeTimeout_PreventsAutoHideDoubleFire()
    {
        SetAutoHideMs(400);
        try
        {
            var coord = CoordinatorTestHelpers.CreateCoordinator(
                out _, out var broker, out _, out _,
                out var capture, out var ocr, out _,
                out var overlay, out var translator, out var popup);

            broker.RaiseHotkeyFired(HotkeyEventType.Word);
            await Task.Delay(30);
            capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
                new PhysicalRect(0, 0, 720, 320), Mid));
            await Task.Delay(30);
            ocr.RecognizeTcs.SetResult(CreateOcrResult(new PhysicalRect(0, 0, 720, 320)));
            await Task.Delay(40);
            translator.TranslateWordTcs.SetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
            await Task.Delay(350);

            // Esc 取消：自动隐藏计时器一并取消，之后不应再有额外汇隐藏
            coord.CancelAll(returnIdle: true);
            int hidesAfterCancel = overlay.HideAllCount;
            await Task.Delay(450);

            Assert.Equal(hidesAfterCancel, overlay.HideAllCount);
            Assert.Equal(AppState.Idle, coord.State);
        }
        finally
        {
            SetAutoHideMs(5000);
        }
    }

    [Fact]
    public async Task NoTextFound_RetriesWithDoubledSize()
    {
        // 首次未识别到文字 → 范围可能太小，双向翻倍重抓一次
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out _, out var broker, out _, out _,
            out var capture, out var ocr, out var selector,
            out var overlay, out var translator, out var popup);

        selector.SelectFunc = (_, _, _) => new SelectionResult(
            Text: null,
            ContextLine: null,
            Box: default,
            Kind: SelectionKind.Word,
            Confidence: null,
            OperationId: Guid.NewGuid(),
            NoTextFound: true);

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(30);
        capture.CaptureAroundTcs.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 186, 80), Mid));
        await Task.Delay(30);
        ocr.RecognizeTcs.SetResult(CreateOcrResult(new PhysicalRect(0, 0, 186, 80)));
        await Task.Delay(80);

        Assert.Equal(2, capture.CaptureAroundCount);
        Assert.Equal(372, capture.CaptureAroundSizes[1].Width);
        Assert.Equal(160, capture.CaptureAroundSizes[1].Height);

        // 重抓后仍未识别到 → 错误提示，不画选框
        Assert.Equal(0, overlay.SelectionShowCount);
        Assert.Equal(1, popup.ShowErrorCount);
    }
}
