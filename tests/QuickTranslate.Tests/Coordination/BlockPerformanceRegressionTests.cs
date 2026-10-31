using Microsoft.Extensions.Options;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Infrastructure.Coordination;
using Xunit;

namespace QuickTranslate.Tests.Coordination;

public class BlockPerformanceRegressionTests
{
    private static readonly MonitorId Monitor = new(new IntPtr(1), "DISPLAY1");

    private sealed class RecordingEngine : IOcrEngine
    {
        public int Calls;
        public int CallingThread;
        public PhysicalRect? LastBand;
        public bool LastForceCpu;
        public CancellationToken LastToken;
        public string EngineName => "Recording";
        public bool IsAvailable => true;
        public event EventHandler? SessionCreated;
        public void RaiseSessionCreated() => SessionCreated?.Invoke(this, EventArgs.Empty);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default, bool forceCpu = false)
            => RecognizeAsync(frame, null, ct, forceCpu);
        public Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, PhysicalRect? focusBand, CancellationToken ct = default, bool forceCpu = false)
        {
            Interlocked.Increment(ref Calls);
            CallingThread = Environment.CurrentManagedThreadId;
            LastBand = focusBand;
            LastToken = ct;
            LastForceCpu = forceCpu;
            return Task.FromResult(new OcrLayoutResult(frame.Region, Array.Empty<OcrLine>(),
                new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
                DateTimeOffset.UtcNow, 96, 96, EngineName));
        }
    }

    private static BlockSelectionResult Block(PhysicalRect box)
        => new("text", box, new[] { new OcrLine(box, Array.Empty<OcrWord>(), "text") });

    private static BlockRetryCoordinator Create(
        RecordingEngine engine, FakeScreenCapture? capture = null,
        FakeMonitorService? monitors = null, FakeBlockSelector? selector = null)
        => new(capture ?? new FakeScreenCapture(), engine, selector ?? new FakeBlockSelector(),
            monitors ?? new FakeMonitorService(), Options.Create(new AppSettings()));

    [Fact]
    public async Task WarmEngineRecognition_DoesNotRunOnCallingThread()
    {
        var engine = new RecordingEngine();
        var coordinator = Create(engine);
        using var frame = FakeScreenCapture.CreateFrame(new PhysicalRect(0, 0, 8, 8), Monitor);
        var returned = new TaskCompletionSource<Task<OcrLayoutResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThread = -1;
        var caller = new Thread(() =>
        {
            try
            {
                callerThread = Environment.CurrentManagedThreadId;
                returned.SetResult(coordinator.Ocr.RecognizeAsync(frame, new PhysicalRect(0, 0, 2, 8)));
            }
            catch (Exception ex) { returned.SetException(ex); }
        }) { IsBackground = true };
        caller.Start();
        var operation = await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(callerThread, engine.CallingThread);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task AlreadyCanceledRecognition_DoesNotStartEngineWork()
    {
        var engine = new RecordingEngine();
        var coordinator = Create(engine);
        using var frame = FakeScreenCapture.CreateFrame(new PhysicalRect(0, 0, 8, 8), Monitor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.Ocr.RecognizeAsync(frame, null, cts.Token));
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Recognition_ForwardsOptionsCancellationAndSessionEvents()
    {
        var engine = new RecordingEngine();
        var coordinator = Create(engine);
        using var frame = FakeScreenCapture.CreateFrame(new PhysicalRect(0, 0, 8, 8), Monitor);
        using var cts = new CancellationTokenSource();
        var band = new PhysicalRect(2, 1, 2, 6);
        int events = 0;
        EventHandler handler = (_, _) => events++;
        coordinator.Ocr.SessionCreated += handler;
        engine.RaiseSessionCreated();
        coordinator.Ocr.SessionCreated -= handler;
        engine.RaiseSessionCreated();
        await coordinator.Ocr.RecognizeAsync(frame, band, cts.Token, forceCpu: true);
        Assert.Equal(1, events);
        Assert.Equal(band, engine.LastBand);
        Assert.Equal(cts.Token, engine.LastToken);
        Assert.True(engine.LastForceCpu);
        Assert.Equal(engine.EngineName, coordinator.Ocr.EngineName);
        Assert.Equal(engine.IsAvailable, coordinator.Ocr.IsAvailable);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public async Task EntireMonitorAlreadyCaptured_DoesNotRecaptureSamePixels(int dpi)
    {
        var region = new PhysicalRect(0, 0, 1200, 720);
        var capture = new FakeScreenCapture { CaptureAroundFunc = (_, _) => FakeScreenCapture.CreateFrame(region, Monitor) };
        var monitors = new FakeMonitorService();
        monitors.Primary = monitors.Primary with { Bounds = region, WorkArea = region, DpiX = (uint)dpi, DpiY = (uint)dpi };
        var selector = new FakeBlockSelector { SelectFunc = (_, _, _) => Block(new PhysicalRect(5, 200, 600, 140)) };
        var engine = new RecordingEngine();
        var coordinator = Create(engine, capture, monitors, selector);
        var result = await coordinator.SelectBlockWithRetryAsync(new PhysicalPoint(300, 280), Monitor, (uint)dpi, (uint)dpi, CancellationToken.None);
        Assert.Equal(1, result.captures);
        Assert.Equal(1, capture.CaptureAroundCount);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task FocusBandAtPhysicalTop_DoesNotExpandTowardUnrelatedLowerRows()
    {
        var region = new PhysicalRect(0, 0, 1200, 720);
        var capture = new FakeScreenCapture { CaptureAroundFunc = (_, _) => FakeScreenCapture.CreateFrame(region, Monitor) };
        var monitors = new FakeMonitorService();
        monitors.Primary = monitors.Primary with { Bounds = region, WorkArea = region };
        var selector = new FakeBlockSelector { SelectFunc = (_, _, _) => Block(new PhysicalRect(400, 5, 400, 300)) };
        var engine = new RecordingEngine();
        var coordinator = Create(engine, capture, monitors, selector);
        var result = await coordinator.SelectBlockWithRetryAsync(new PhysicalPoint(600, 280), Monitor, 96, 96, CancellationToken.None);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(1, result.captures);
        Assert.False(result.block.NoBlockFound);
    }

    [Fact]
    public async Task GenuinelyClippedCrop_StillRetries()
    {
        var first = new PhysicalRect(200, 0, 1200, 720);
        var larger = new PhysicalRect(0, 0, 1680, 1008);
        int captureCalls = 0;
        var capture = new FakeScreenCapture
        {
            CaptureAroundFunc = (_, _) => FakeScreenCapture.CreateFrame(++captureCalls == 1 ? first : larger, Monitor)
        };
        int selectionCalls = 0;
        var selector = new FakeBlockSelector
        {
            SelectFunc = (_, _, _) => Block(++selectionCalls == 1
                ? new PhysicalRect(205, 200, 800, 180) : new PhysicalRect(150, 200, 855, 180))
        };
        var engine = new RecordingEngine();
        var coordinator = Create(engine, capture, new FakeMonitorService(), selector);
        var result = await coordinator.SelectBlockWithRetryAsync(new PhysicalPoint(800, 300), Monitor, 96, 96, CancellationToken.None);
        Assert.Equal(2, result.captures);
        Assert.Equal(2, engine.Calls);
        Assert.Equal(150, result.block.UnionBox.Left);
    }
}
