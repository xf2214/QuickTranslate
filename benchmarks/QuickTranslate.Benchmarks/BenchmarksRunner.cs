using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Coordination;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Capture;
using QuickTranslate.Core.Common;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Ocr;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;

namespace QuickTranslate.Benchmarks;

public class BenchmarksRunner
{
    private readonly bool _fastMode;
    private readonly string _repoRoot;

    public BenchmarksRunner(bool fastMode, string repoRoot)
    {
        _fastMode = fastMode;
        _repoRoot = repoRoot;
    }

    public List<BenchmarkMetric> RunAll()
    {
        var metrics = new List<BenchmarkMetric>();

        metrics.AddRange(MeasureIdleMetrics());
        metrics.Add(MeasureWordSelector());
        metrics.Add(MeasureBlockSelector());
        metrics.Add(MeasureHotkeyToOverlay());
        metrics.AddRange(MeasureSqliteCache());
        metrics.Add(MeasureCancelLatency());

        return metrics;
    }

    private static double Percentile(List<double> samples, double p)
    {
        if (samples.Count == 0) return 0;
        var sorted = samples.OrderBy(x => x).ToList();
        int idx = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        idx = Math.Max(0, Math.Min(sorted.Count - 1, idx));
        return sorted[idx];
    }

    private List<BenchmarkMetric> MeasureIdleMetrics()
    {
        int seconds = _fastMode ? 3 : 10;
        var workingSetSamples = new List<double>();
        var cpuSamples = new List<double>();

        var proc = Process.GetCurrentProcess();
        var prevCpuTime = proc.TotalProcessorTime;
        var prevTime = Stopwatch.GetTimestamp();

        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            proc.Refresh();

            workingSetSamples.Add(proc.WorkingSet64 / (1024.0 * 1024.0));

            var currCpuTime = proc.TotalProcessorTime;
            var currTime = Stopwatch.GetTimestamp();
            double elapsedSec = (currTime - prevTime) / (double)Stopwatch.Frequency;
            double cpuDeltaMs = (currCpuTime - prevCpuTime).TotalMilliseconds;
            double cpuPercent = cpuDeltaMs / (elapsedSec * 1000.0 * Environment.ProcessorCount) * 100.0;
            cpuSamples.Add(cpuPercent);

            prevCpuTime = currCpuTime;
            prevTime = currTime;
        }

        return new List<BenchmarkMetric>
        {
            new()
            {
                Name = "IdleWorkingSet",
                Unit = "MB",
                P50 = Percentile(workingSetSamples, 50),
                P95 = Percentile(workingSetSamples, 95),
                SampleCount = workingSetSamples.Count,
                Notes = $"{seconds}s sample, 1Hz, Process.WorkingSet64",
                Samples = workingSetSamples
            },
            new()
            {
                Name = "IdleCpu",
                Unit = "%",
                P50 = Percentile(cpuSamples, 50),
                P95 = Percentile(cpuSamples, 95),
                SampleCount = cpuSamples.Count,
                Notes = $"{seconds}s sample, 1Hz, TotalProcessorTime delta / (Nproc * 1s)",
                Samples = cpuSamples
            }
        };
    }

    private static List<OcrLayoutResult> BuildOcrLayoutResults(int count)
    {
        var results = new List<OcrLayoutResult>();
        var rng = new Random(42);

        for (int i = 0; i < count; i++)
        {
            var lines = new List<OcrLine>();
            int baseY = 50;

            for (int li = 0; li < 15; li++)
            {
                int lineHeight = 24 + rng.Next(0, 8);
                int y = baseY + li * (lineHeight + 6);
                var words = new List<OcrWord>();
                int x = 100;
                int wordCount = 5 + rng.Next(0, 6);

                for (int wi = 0; wi < wordCount; wi++)
                {
                    int ww = 30 + rng.Next(10, 80);
                    words.Add(new OcrWord(
                        new PhysicalRect(x, y, ww, lineHeight),
                        $"word{li}_{wi}",
                        0.7f + (float)rng.NextDouble() * 0.3f,
                        li));
                    x += ww + 8;
                }

                lines.Add(new OcrLine(
                    new PhysicalRect(100, y, x - 100 - 8, lineHeight),
                    words));
            }

            for (int hi = 0; hi < 3; hi++)
            {
                int y = 20 + hi * 32;
                lines.Insert(0, new OcrLine(
                    new PhysicalRect(100, y, 300 + hi * 50, 28),
                    new List<OcrWord>
                    {
                        new(new PhysicalRect(100, y, 300 + hi * 50, 28), $"Title{hi}", 0.99f, 0)
                    },
                    $"This is Title {hi}"));
            }

            for (int fi = 0; fi < 2; fi++)
            {
                int y = 600 + fi * 28;
                lines.Add(new OcrLine(
                    new PhysicalRect(200, y, 500, 22),
                    new List<OcrWord>
                    {
                        new(new PhysicalRect(200, y, 500, 22), $"Footnote{fi}", 0.85f, 0)
                    },
                    $"Footnote text {fi}"));
            }

            results.Add(new OcrLayoutResult(
                new PhysicalRect(0, 0, 1200, 800),
                lines.AsReadOnly(),
                new OcrTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
                DateTimeOffset.Now,
                96, 96, "BenchmarkMock"));
        }

        return results;
    }

    private static List<PhysicalPoint> BuildAnchors(int count, OcrLayoutResult ocr)
    {
        var anchors = new List<PhysicalPoint>();
        var rng = new Random(123);

        for (int i = 0; i < count; i++)
        {
            if (ocr.Lines.Count > 0 && rng.Next(2) == 0)
            {
                var line = ocr.Lines[rng.Next(ocr.Lines.Count)];
                if (line.Words.Count > 0)
                {
                    var word = line.Words[rng.Next(line.Words.Count)];
                    anchors.Add(new PhysicalPoint(
                        word.Box.X + word.Box.Width / 2,
                        word.Box.Y + word.Box.Height / 2));
                    continue;
                }
            }
            anchors.Add(new PhysicalPoint(
                100 + rng.Next(0, 1000),
                50 + rng.Next(0, 700)));
        }

        return anchors;
    }

    private BenchmarkMetric MeasureWordSelector()
    {
        int layoutCount = _fastMode ? 5 : 20;
        int anchorsPerLayout = _fastMode ? 5 : 20;

        var layouts = BuildOcrLayoutResults(layoutCount);
        var resolver = new DefaultWordBoxResolver();
        var selector = new WordSelector(resolver);
        var samples = new List<double>();
        var sw = new Stopwatch();

        for (int i = 0; i < 3; i++)
        {
            selector.SelectWord(layouts[0], BuildAnchors(1, layouts[0])[0], null);
        }

        foreach (var layout in layouts)
        {
            var anchors = BuildAnchors(anchorsPerLayout, layout);
            foreach (var anchor in anchors)
            {
                sw.Restart();
                selector.SelectWord(layout, anchor, null);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMicroseconds);
            }
        }

        return new BenchmarkMetric
        {
            Name = "WordSelector",
            Unit = "us",
            P50 = Percentile(samples, 50),
            P95 = Percentile(samples, 95),
            SampleCount = samples.Count,
            Notes = $"{layoutCount} layouts x {anchorsPerLayout} anchors, Stopwatch.Elapsed.TotalMicroseconds",
            Samples = samples
        };
    }

    private BenchmarkMetric MeasureBlockSelector()
    {
        int layoutCount = _fastMode ? 5 : 20;
        int anchorsPerLayout = _fastMode ? 5 : 20;

        var layouts = BuildOcrLayoutResults(layoutCount);
        var selector = new DefaultBlockSelector();
        var samples = new List<double>();
        var sw = new Stopwatch();

        for (int i = 0; i < 3; i++)
        {
            selector.SelectBlock(layouts[0], BuildAnchors(1, layouts[0])[0], null);
        }

        foreach (var layout in layouts)
        {
            var anchors = BuildAnchors(anchorsPerLayout, layout);
            foreach (var anchor in anchors)
            {
                sw.Restart();
                selector.SelectBlock(layout, anchor, null);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMicroseconds);
            }
        }

        return new BenchmarkMetric
        {
            Name = "BlockSelector",
            Unit = "us",
            P50 = Percentile(samples, 50),
            P95 = Percentile(samples, 95),
            SampleCount = samples.Count,
            Notes = $"{layoutCount} layouts x {anchorsPerLayout} anchors, Stopwatch.Elapsed.TotalMicroseconds",
            Samples = samples
        };
    }

    private class BenchFakeCursorService : ICursorService
    {
        public PhysicalPoint FixedPos { get; set; } = new(400, 300);
        public MonitorId FixedMonitor { get; set; } = new(new IntPtr(1), @"\\.\DISPLAY1");

        public PhysicalPoint GetPhysicalCursorPos(out MonitorId monitorId)
        {
            monitorId = FixedMonitor;
            return FixedPos;
        }
    }

    private class BenchFakeMonitorService : IMonitorService
    {
        public MonitorInfo PrimaryInfo { get; } = new(
            new MonitorId(new IntPtr(1), @"\\.\DISPLAY1"),
            @"\\.\DISPLAY1",
            new PhysicalRect(0, 0, 1920, 1080),
            new PhysicalRect(0, 0, 1920, 1040),
            96, 96, true);

        public IReadOnlyList<MonitorInfo> EnumerateMonitors() => new[] { PrimaryInfo };
        public MonitorInfo? TryGetMonitorFromPoint(PhysicalPoint pt) => PrimaryInfo;
        public MonitorInfo? TryGetPrimary() => PrimaryInfo;
        public MonitorId MonitorFromWindow(IntPtr hwnd) => PrimaryInfo.Id;
    }

    private class BenchFakeScreenCapture : IScreenCapture
    {
        public Task<ScreenFrame> CaptureAroundAsync(PhysicalPoint anchor, PhysicalSize size, CancellationToken ct = default)
        {
            var region = new PhysicalRect(
                Math.Max(0, anchor.X - size.Width / 2),
                Math.Max(0, anchor.Y - size.Height / 2),
                size.Width, size.Height);
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            return Task.FromResult(new ScreenFrame(bmp, region, new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));
        }

        public Task<ScreenFrame> CaptureRectAsync(PhysicalRect region, MonitorId? monitorHint = null, CancellationToken ct = default)
        {
            var bmp = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height), PixelFormat.Format32bppArgb);
            return Task.FromResult(new ScreenFrame(bmp, region, monitorHint ?? MonitorId.Empty));
        }
    }

    private class BenchFakeOcrEngine : IOcrEngine
    {
        public string EngineName => "BenchMockOcr";
        public bool IsAvailable => true;
        public event EventHandler? SessionCreated;

        public async Task<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, CancellationToken ct = default)
        {
            await Task.Delay(100, ct);
            return BuildOcrLayoutResults(1)[0] with
            {
                CaptureRegion = frame.Region,
                CaptureTime = DateTimeOffset.Now
            };
        }

        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private class BenchOverlayTracker : ISelectionOverlayService
    {
        public int ShowCount { get; private set; }
        public event Action? OnShow;

        public void Show(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96, bool preview = false)
        {
            ShowCount++;
            OnShow?.Invoke();
        }

        public void Update(PhysicalRect physicalBox, MonitorId monitorId, uint dpiX = 96, uint dpiY = 96) { }
        public void Hide(MonitorId monitorId) { }
        public void HideAll() { }
        public IReadOnlyDictionary<MonitorId, (IntPtr hwnd, DipRect lastDipRect)> Inspect()
            => new Dictionary<MonitorId, (IntPtr, DipRect)>();
    }

    private class BenchFakeWordPopupService : IWordPopupService
    {
        public int ShowCount { get; private set; }
        public void Show(SelectionResult selection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX = 96, uint dpiY = 96) => ShowCount++;
        public void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId) { }
        public void Hide() { }
        public void HideAll() { }
    }

    private class BenchFakeTranslationRouter : ITranslationRouter
    {
        public Task<TranslationResult> TranslateWordAsync(string word, string targetLang, CancellationToken ct = default)
        {
            return Task.FromResult(new TranslationResult(
                word.ToLowerInvariant(),
                word,
                "mock-" + word,
                targetLang,
                false, false, false));
        }

        public Task<TranslationResult> TranslateBlockAsync(string blockText, string targetLang, CancellationToken ct = default)
        {
            return Task.FromResult(new TranslationResult(
                blockText.ToLowerInvariant(),
                blockText,
                "mock-block-translation",
                targetLang,
                false, false, false));
        }
    }

    private class BenchFakeAppLifecycle : IAppLifecycle
    {
        public bool IsPaused { get; private set; }
        public event EventHandler? Paused;
        public event EventHandler? Resumed;
        public event EventHandler<int>? ShuttingDown;
        public void Pause() { IsPaused = true; Paused?.Invoke(this, EventArgs.Empty); }
        public void Resume() { IsPaused = false; Resumed?.Invoke(this, EventArgs.Empty); }
        public void Shutdown(int exitCode = 0) => ShuttingDown?.Invoke(this, exitCode);
        public void SetPaused(bool paused) => IsPaused = paused;
    }

    private class BenchFakeHotkeyBroker : IHotkeyBroker
    {
        public event EventHandler<HotkeyEvent>? HotkeyFired;
        public void RegisterDefaultsFromSettings(AppSettings settings) { }
        public Result TryUpdateHotkeys(HotkeyCombo newWord, HotkeyCombo newBlock) => Result.Ok();
        public bool Probe(HotkeyModifiers mods, KeyboardKey key) => true;
        public void UnregisterAll() { }
        public void Fire(HotkeyEventType t) => HotkeyFired?.Invoke(this, new HotkeyEvent(t, DateTimeOffset.Now));
    }

    private BenchmarkMetric MeasureHotkeyToOverlay()
    {
        int warmupCount = 5;
        int runCount = _fastMode ? 5 : 20;
        var samples = new List<double>();

        for (int i = 0; i < warmupCount + runCount; i++)
        {
            var appLifecycle = new BenchFakeAppLifecycle();
            var broker = new BenchFakeHotkeyBroker();
            var cursor = new BenchFakeCursorService();
            var monitors = new BenchFakeMonitorService();
            var capture = new BenchFakeScreenCapture();
            var ocr = new BenchFakeOcrEngine();
            var wordSelector = new WordSelector(new DefaultWordBoxResolver());
            var overlay = new BenchOverlayTracker();
            var translator = new BenchFakeTranslationRouter();
            var popup = new BenchFakeWordPopupService();
            var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
            var logger = NullLogger<WordInteractionCoordinator>.Instance;

            var coord = new WordInteractionCoordinator(
                appLifecycle, broker, cursor, monitors, capture, ocr, wordSelector, overlay, translator, popup, settings, logger);

            int showCountBefore = overlay.ShowCount;
            bool isMeasurement = i >= warmupCount;
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            overlay.OnShow += () => tcs.TrySetResult(overlay.ShowCount);

            var sw = Stopwatch.StartNew();
            broker.Fire(HotkeyEventType.Word);

            bool completed = tcs.Task.Wait(5000);
            sw.Stop();

            if (completed && isMeasurement)
            {
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            coord.CancelAll(true);
        }

        if (samples.Count == 0)
        {
            samples.Add(0);
        }

        return new BenchmarkMetric
        {
            Name = "HotkeyToOverlay",
            Unit = "ms",
            P50 = Percentile(samples, 50),
            P95 = Percentile(samples, 95),
            SampleCount = samples.Count,
            Notes = $"WordInteractionCoordinator pipeline, Hotkey→first Overlay.Show (即时反馈路径：选区覆盖层/扫描动画在 OCR 前展示，不含 OCR/翻译耗时), {warmupCount} warmup + {runCount} measured",
            Samples = samples
        };
    }

    private List<BenchmarkMetric> MeasureSqliteCache()
    {
        int opCount = _fastMode ? 200 : 1000;
        var addSamples = new List<double>();
        var getSamples = new List<double>();
        var sw = new Stopwatch();

        string dbPath = Path.Combine(
            Path.GetTempPath(),
            $"qt-bench-sqlite-{Guid.NewGuid():N}.db");

        try
        {
            var logger = NullLogger<SqliteTranslationCache>.Instance;
            using var cache = new SqliteTranslationCache(dbPath, 10000, logger);

            var rng = new Random(789);
            var keys = new List<string>();
            var results = new List<TranslationResult>();

            for (int i = 0; i < opCount; i++)
            {
                char[] chars = new char[50];
                for (int j = 0; j < 50; j++)
                    chars[j] = (char)('a' + rng.Next(0, 26));
                keys.Add(new string(chars));
                results.Add(new TranslationResult(
                    keys[i],
                    keys[i],
                    "translated-" + keys[i],
                    "zh-CN",
                    false, false, false));
            }

            for (int i = 0; i < 10; i++)
            {
                cache.AddAsync($"warmup-{i}", new TranslationResult($"w{i}", $"w{i}", $"tw{i}", "zh")).Wait();
                cache.TryGetAsync($"warmup-{i}").Wait();
            }

            for (int i = 0; i < opCount; i++)
            {
                sw.Restart();
                cache.AddAsync(keys[i], results[i]).Wait();
                sw.Stop();
                addSamples.Add(sw.Elapsed.TotalMicroseconds);
            }

            for (int i = 0; i < opCount; i++)
            {
                sw.Restart();
                cache.TryGetAsync(keys[i]).Wait();
                sw.Stop();
                getSamples.Add(sw.Elapsed.TotalMicroseconds);
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }

        return new List<BenchmarkMetric>
        {
            new()
            {
                Name = "SqliteCacheAdd",
                Unit = "us",
                P50 = Percentile(addSamples, 50),
                P95 = Percentile(addSamples, 95),
                SampleCount = addSamples.Count,
                Notes = $"{opCount} random 50-char keys, AddAsync (Upsert), temp db deleted after",
                Samples = addSamples
            },
            new()
            {
                Name = "SqliteCacheGet",
                Unit = "us",
                P50 = Percentile(getSamples, 50),
                P95 = Percentile(getSamples, 95),
                SampleCount = getSamples.Count,
                Notes = $"{opCount} same-key lookups after Add, TryGetAsync incl. UPDATE last_hit/hits",
                Samples = getSamples
            }
        };
    }

    private class BenchFakeBlockPopupService : IBlockPopupService
    {
        public int ShowCount { get; private set; }
        public void Show(BlockSelectionResult blockSelection, TranslationResult translation, MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY) => ShowCount++;
        public void ShowError(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string shortMessage, Guid operationId) { }
        public void HideAll() { }
    }

    private BenchmarkMetric MeasureCancelLatency()
    {
        int runCount = _fastMode ? 10 : 20;
        var samples = new List<double>();

        for (int i = 0; i < runCount; i++)
        {
            var broker = new BenchFakeHotkeyBroker();
            var cursor = new BenchFakeCursorService();
            var monitors = new BenchFakeMonitorService();
            var capture = new BenchFakeScreenCapture();
            var ocr = new BenchFakeOcrEngine();
            var wordSelector = new WordSelector(new DefaultWordBoxResolver());
            var overlay = new BenchOverlayTracker();
            var translator = new BenchFakeTranslationRouter();
            var popup = new BenchFakeWordPopupService();
            var blockPopup = new BenchFakeBlockPopupService();
            var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });
            var logger = NullLogger<WordInteractionCoordinator>.Instance;
            var appLifecycle = new BenchFakeAppLifecycle();

            var coord = new WordInteractionCoordinator(
                appLifecycle, broker, cursor, monitors, capture, ocr, wordSelector, overlay, translator, popup, settings, logger);

            broker.Fire(HotkeyEventType.Word);
            Thread.Sleep(10);

            var sw = Stopwatch.StartNew();
            var slot = coord.CurrentSlot;
            if (slot != null)
            {
                slot.Cts.Cancel();
                try
                {
                    while (!slot.Cts.Token.IsCancellationRequested)
                    {
                        Thread.SpinWait(1);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMicroseconds);

            coord.CancelAll(true);
        }

        return new BenchmarkMetric
        {
            Name = "CancelLatency",
            Unit = "us",
            P50 = Percentile(samples, 50),
            P95 = Percentile(samples, 95),
            SampleCount = samples.Count,
            Notes = "Fire Word hotkey → Sleep 10ms → slot.Cts.Cancel → until IsCancellationRequested",
            Samples = samples
        };
    }
}
