using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
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
using QuickTranslate.Tests.Coordination;
using QuickTranslate.Tests.Infrastructure;
using Xunit;

namespace QuickTranslate.Tests.App;

public class GlobalExceptionFallbackTests
{
    [Fact]
    public void DispatcherUnhandled_Exception_Handled_AndLogged()
    {
        var entries = new List<SpyLogEntry>();
        var loggerProvider = new SpyLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var logger = factory.CreateLogger("Test");

        bool handledFlag = false;
        Exception? observedException = null;

        var dispatcherSimulator = new DispatcherExceptionSimulator();
        dispatcherSimulator.DispatcherUnhandledException += (s, e) =>
        {
            logger.LogError(e.Exception, "Dispatcher Unhandled Exception");
            e.Handled = true;
            handledFlag = true;
            observedException = e.Exception;
        };

        var testEx = new InvalidOperationException("test dispatcher exception");

        try
        {
            dispatcherSimulator.SimulateException(testEx);
        }
        catch (InvalidOperationException)
        {
            Assert.Fail("Handled exception should not propagate out");
        }

        Assert.True(handledFlag, "Dispatcher exception should be marked Handled");
        Assert.Same(testEx, observedException);

        var errorLogs = loggerProvider.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Single(errorLogs);
        Assert.Contains("Dispatcher Unhandled Exception", errorLogs[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_OcrInferenceFailed_NotCrash_ErrorLogged()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<WordInteractionCoordinator>(entries);
        var appLifecycle = new FakeAppLifecycle();
        var broker = new FakeHotkeyBroker();
        var cursor = new FakeCursorService();
        var monitors = new FakeMonitorService();
        var capture = new FakeScreenCapture();
        var ocr = new FakeOcrEngine();
        var selector = new FakeWordSelector();
        var overlay = new FakeOverlayService();
        var translator = new FakeTranslationRouter();
        var popup = new FakePopupService();
        var settings = Options.Create(new AppSettings { TargetLanguage = "zh-CN" });

        ocr.InferenceFailureFactory = () => new InvalidDataException("corrupt ocr data");

        var coord = new WordInteractionCoordinator(
            appLifecycle, broker, cursor, monitors, capture, ocr, selector, overlay,
            translator, popup, settings, logger);

        var captureDone = new TaskCompletionSource<ScreenFrame>();
        capture.CaptureAroundTcs = captureDone;
        ocr.RecognizeTcs = new TaskCompletionSource<OcrLayoutResult>();
        translator.TranslateWordTcs = new TaskCompletionSource<TranslationResult>();

        broker.RaiseHotkeyFired(HotkeyEventType.Word);
        await Task.Delay(20);

        captureDone.SetResult(FakeScreenCapture.CreateFrame(
            new PhysicalRect(0, 0, 720, 320), new MonitorId(new IntPtr(1), @"\\.\DISPLAY1")));

        var timeoutCts = new CancellationTokenSource(3000);
        while (coord.State != AppState.Idle && !timeoutCts.IsCancellationRequested)
        {
            await Task.Delay(20, timeoutCts.Token);
        }

        Assert.Equal(AppState.Idle, coord.State);
        Assert.Equal(1, popup.ShowErrorCount);

        var errorCall = popup.ShowErrorCalls[0];
        Assert.DoesNotContain("StackTrace", errorCall.Msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at ", errorCall.Msg, StringComparison.OrdinalIgnoreCase);

        var errorLogs = entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(1, errorLogs.Count);
    }

    public class DispatcherExceptionSimulator
    {
        public event DispatcherUnhandledExceptionEventHandler? DispatcherUnhandledException;

        public void SimulateException(Exception ex)
        {
            var args = new DispatcherUnhandledExceptionEventArgs(ex, isHandled: false);
            DispatcherUnhandledException?.Invoke(this, args);
            if (!args.Handled)
            {
                throw ex;
            }
        }
    }

    public delegate void DispatcherUnhandledExceptionEventHandler(object sender, DispatcherUnhandledExceptionEventArgs e);

    public class DispatcherUnhandledExceptionEventArgs : EventArgs
    {
        public Exception Exception { get; }
        public bool Handled { get; set; }

        public DispatcherUnhandledExceptionEventArgs(Exception exception, bool isHandled)
        {
            Exception = exception;
            Handled = isHandled;
        }
    }
}
