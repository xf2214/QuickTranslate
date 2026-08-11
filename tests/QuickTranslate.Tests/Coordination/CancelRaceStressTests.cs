using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Coordination;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class CancelRaceStressTests
{
    private static BlockInteractionCoordinator CreateBlockCoordinator(
        out FakeCursorService cursor,
        out FakeMonitorService monitors,
        out FakeScreenCapture capture,
        out FakeOcrEngine ocr,
        out FakeBlockSelector blockSelector,
        out FakeOverlayService overlay,
        out FakeBlockPopupService popup,
        out FakeTranslationRouter translator,
        out FakeEscHook escHook)
    {
        cursor = new FakeCursorService();
        monitors = new FakeMonitorService();
        capture = new FakeScreenCapture();
        ocr = new FakeOcrEngine();
        blockSelector = new FakeBlockSelector();
        overlay = new FakeOverlayService();
        popup = new FakeBlockPopupService();
        translator = new FakeTranslationRouter();
        escHook = new FakeEscHook();

        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
        var logger = NullLogger<BlockInteractionCoordinator>.Instance;
        var retryLogger = NullLogger<BlockRetryCoordinator>.Instance;

        var retryCoord = new BlockRetryCoordinator(capture, ocr, blockSelector, monitors, settings);

        return new BlockInteractionCoordinator(
            cursor, monitors, retryCoord, overlay, popup, translator, settings, logger, escHook);
    }

    [Fact]
    public async Task CancelRace_100Rounds_NoDeadlock_NoMemoryBlowup()
    {
        var slotRefs = new List<WeakReference>();
        var ctsRefs = new List<WeakReference>();
        int rounds = 100;

        for (int i = 0; i < rounds; i++)
        {
            var coord = CreateBlockCoordinator(
                out _, out _, out var capture, out var ocr, out _,
                out _, out _, out var translator, out var escHook);

            coord.Start();

            capture.CaptureAroundTcs = new TaskCompletionSource<ScreenFrame>();
            ocr.RecognizeTcs = new TaskCompletionSource<OcrLayoutResult>();
            translator.TranslateBlockTcs = new TaskCompletionSource<TranslationResult>();

            coord.RunBlockPipeline();
            await Task.Delay(20);

            if (coord.CurrentSlot != null)
            {
                slotRefs.Add(new WeakReference(coord.CurrentSlot));
                ctsRefs.Add(new WeakReference(coord.CurrentSlot.Cts));
            }

            escHook.RaiseEscPressed();
            await Task.Delay(20);

            coord.Stop();
            coord.Dispose();
        }

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();

        int aliveSlots = slotRefs.Count(r => r.IsAlive);
        int aliveCts = ctsRefs.Count(r => r.IsAlive);

        Assert.True(aliveSlots <= 1, $"Expected <= 1 alive slots after GC, got {aliveSlots}");
        Assert.True(aliveCts <= 1, $"Expected <= 1 alive CTS after GC, got {aliveCts}");
    }

    [Fact]
    public async Task HighFrequencyCancellation_100Rounds_UIDispatchable()
    {
        int unobservedExceptions = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (s, e) =>
        {
            Interlocked.Increment(ref unobservedExceptions);
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            int rounds = 100;
            var allTasks = new List<Task>();

            for (int i = 0; i < rounds; i++)
            {
                var coord = CreateBlockCoordinator(
                    out _, out _, out var capture, out var ocr, out var blockSel,
                    out var overlay, out var popup, out var translator, out var escHook);

                coord.Start();

                var tcs = new TaskCompletionSource<bool>();

                capture.CaptureAroundTcs = new TaskCompletionSource<ScreenFrame>();
                ocr.RecognizeTcs = new TaskCompletionSource<OcrLayoutResult>();
                translator.TranslateBlockTcs = new TaskCompletionSource<TranslationResult>();

                blockSel.SelectFunc = (o, p, opt) => new BlockSelectionResult(
                    BlockText: "hello world",
                    UnionBox: new PhysicalRect(100, 100, 200, 100),
                    SelectedLines: Array.Empty<OcrLine>(),
                    Kind: SelectionKind.Block,
                    OperationId: Guid.NewGuid(),
                    NoBlockFound: false);

                var runTask = Task.Run(() =>
                {
                    try
                    {
                        coord.RunBlockPipeline();
                        Thread.Sleep(5);
                        escHook.RaiseEscPressed();
                        tcs.TrySetResult(true);
                    }
                    catch
                    {
                        tcs.TrySetResult(true);
                    }
                });

                allTasks.Add(runTask);
                allTasks.Add(tcs.Task);

                await Task.WhenAny(runTask, Task.Delay(100));

                coord.Stop();
                coord.Dispose();
            }

            await Task.WhenAll(allTasks);

            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(50);

            Assert.Equal(0, Volatile.Read(ref unobservedExceptions));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    public class FakeBlockSelector : IBlockSelector
    {
        public Func<OcrLayoutResult, PhysicalPoint, SelectionOptions?, BlockSelectionResult>? SelectFunc { get; set; }

        public BlockSelectionResult SelectBlock(OcrLayoutResult ocr, PhysicalPoint anchor, SelectionOptions? opts = null)
        {
            if (SelectFunc != null) return SelectFunc(ocr, anchor, opts);
            return new BlockSelectionResult(
                BlockText: null,
                UnionBox: default,
                SelectedLines: Array.Empty<OcrLine>(),
                Kind: SelectionKind.Block,
                OperationId: Guid.NewGuid(),
                NoBlockFound: true);
        }
    }
}
