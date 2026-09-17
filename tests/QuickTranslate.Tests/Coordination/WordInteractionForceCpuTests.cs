using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Options;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class WordInteractionForceCpuTests
{
    private static ScreenFrame CreateLargeFrame(int w = 800, int h = 600, int x = 0, int y = 0)
    {
        var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        var region = new PhysicalRect(x, y, w, h);
        return new ScreenFrame(bmp, region, MonitorId.Empty);
    }

    [Fact]
    public async Task WordCoordinator_FirstRecognize_AlwaysForceCpu_EvenWithLargeFrameAndHighDpi()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out var appLifecycle, out var broker, out var cursor, out var monitors,
            out var capture, out var ocr, out var selector, out var overlay, out var translator, out var popup);
        monitors.Primary = monitors.Primary with { DpiX = 192, DpiY = 192 };
        cursor.CursorPos = new PhysicalPoint(960, 540);
        capture.QueuedFrames.Enqueue(CreateLargeFrame(800, 600));
        selector.SelectFunc = (ocrRes, anchor, opts) => new SelectionResult(
            Text: "hello", ContextLine: "hello", Box: new PhysicalRect(940, 530, 40, 20),
            Kind: SelectionKind.Word, Confidence: 0.9f, OperationId: Guid.NewGuid(), NoTextFound: false);
        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(120);
        if (!ocr.RecognizeTcs.Task.IsCompleted)
            ocr.RecognizeTcs.TrySetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 800, 600)));
        await Task.Delay(50);
        translator.TranslateWordTcs.TrySetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
        await Task.Delay(80);
        Assert.True(ocr.FocusBands.Count >= 1, "should have at least one Recognize call");
        Assert.True(ocr.ForceCpuCalls.Count >= 1, "FakeOcrEngine should track ForceCpu");
        Assert.All(ocr.ForceCpuCalls, v => Assert.True(v, "word Recognize must be forceCpu:true"));
        Assert.All(ocr.RecognizeHolderChoices, ep => Assert.Equal("CPU", ep));
        coord.CancelAll(returnIdle: true);
    }

    [Fact]
    public async Task WordCoordinator_RetryRecognize_AlsoForceCpu()
    {
        var coord = CoordinatorTestHelpers.CreateCoordinator(
            out var appLifecycle, out var broker, out var cursor, out var monitors,
            out var capture, out var ocr, out var selector, out var overlay, out var translator, out var popup);
        monitors.Primary = monitors.Primary with { DpiX = 192, DpiY = 192 };
        cursor.CursorPos = new PhysicalPoint(50, 50);
        capture.QueuedFrames.Enqueue(CreateLargeFrame(800, 600, 0, 0));
        capture.QueuedFrames.Enqueue(CreateLargeFrame(1600, 1200, -400, -300));
        int selCalls = 0;
        selector.SelectFunc = (ocrRes, anchor, opts) =>
        {
            selCalls++;
            if (selCalls == 1)
                return new SelectionResult("clip", "clip", new PhysicalRect(0, 0, 10, 20), SelectionKind.Word, 0.9f, Guid.NewGuid());
            return new SelectionResult("hello", "hello", new PhysicalRect(10, 10, 40, 20), SelectionKind.Word, 0.95f, Guid.NewGuid());
        };
        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(200);
        if (ocr.RecognizeCount < 2)
        {
            if (!ocr.RecognizeTcs.Task.IsCompleted)
                ocr.RecognizeTcs.TrySetResult(FakeOcrEngine.CreateResult(new PhysicalRect(0, 0, 1600, 1200)));
            await Task.Delay(80);
        }
        if (!translator.TranslateWordTcs.Task.IsCompleted)
            translator.TranslateWordTcs.TrySetResult(FakeTranslationRouter.CreateResult("hello", "zh-CN"));
        await Task.Delay(80);
        Assert.True(ocr.ForceCpuCalls.Count >= 2, $"expected >=2 forceCpu tracked calls, got {ocr.ForceCpuCalls.Count}");
        Assert.All(ocr.ForceCpuCalls, v => Assert.True(v, "retry Recognize must also be forceCpu:true"));
        Assert.All(ocr.RecognizeHolderChoices, ep => Assert.Equal("CPU", ep));
        coord.CancelAll(returnIdle: true);
    }

    [Fact]
    public async Task MockOcrEngine_RecognizeAsync_WithForceCpu_DoesNotThrowAndRespectsSignature()
    {
        var logger = NullLogger<QuickTranslate.Infrastructure.Ocr.MockOcrEngine>.Instance;
        var engine = new QuickTranslate.Infrastructure.Ocr.MockOcrEngine(logger);
        using var frame = CreateLargeFrame(800, 600);
        var r1 = await engine.RecognizeAsync(frame);
        Assert.NotNull(r1);
        var r2 = await ((QuickTranslate.Core.Abstractions.IOcrEngine)engine).RecognizeAsync(frame, null, CancellationToken.None, forceCpu: true);
        Assert.NotNull(r2);
    }

    [Fact]
    public void IOcrEngine_RecognizeAsync_HasForceCpuOptionalParam_DefaultFalse()
    {
        var method = typeof(IOcrEngine).GetMethod("RecognizeAsync", new[] { typeof(ScreenFrame), typeof(PhysicalRect?), typeof(CancellationToken), typeof(bool) });
        if (method == null)
        {
            var methods = typeof(IOcrEngine).GetMethods().Where(m => m.Name == "RecognizeAsync").ToList();
            var hasForce = methods.Any(m => m.GetParameters().Any(p => p.Name == "forceCpu" && p.ParameterType == typeof(bool)));
            Assert.True(hasForce, "IOcrEngine.RecognizeAsync should have bool forceCpu param");
        }
        else
        {
            var p = method.GetParameters().First(x => x.Name == "forceCpu");
            Assert.True(p.HasDefaultValue, "forceCpu should be optional");
            Assert.Equal(false, p.DefaultValue);
        }
    }
}

