using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

[Collection("OcrModelTests")]
public class OcrEngineFallbackTests
{
    private const string DisableDevModelPathsEnv = "QUICKTRANSLATE_DISABLE_DEV_MODEL_PATHS";

    private static IDisposable PinDevModelPathsDisabled()
    {
        var prev = Environment.GetEnvironmentVariable(DisableDevModelPathsEnv);
        Environment.SetEnvironmentVariable(DisableDevModelPathsEnv, "1");
        return new Disposable(() => Environment.SetEnvironmentVariable(DisableDevModelPathsEnv, prev));
    }

    private sealed class Disposable : IDisposable
    {
        private readonly Action _restore;
        public Disposable(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }

    private class TestAppDataProvider : IAppDataProvider
    {
        private readonly string _appDataDir;
        private readonly string _logDir;

        public TestAppDataProvider(string appDataDir, string logDir)
        {
            _appDataDir = appDataDir;
            _logDir = logDir;
        }

        public string GetAppDataDirectory() => _appDataDir;
        public string GetLogDirectory() => _logDir;
    }

    private static IServiceProvider BuildServiceProvider(IAppDataProvider appData)
    {
        var services = new ServiceCollection();

        services.AddSingleton(appData);
        services.AddSingleton(Options.Create(new AppSettings()));

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        services.AddSingleton(loggerFactory);

        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddProvider(new SpyLoggerProvider());
        });

        services.AddOcrEngines();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void PaddleOcrV6Engine_MissingModels_IsAvailableFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(tempDir, "logs");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var _ = PinDevModelPathsDisabled();

            var appData = new TestAppDataProvider(tempDir, logDir);
            var sp = BuildServiceProvider(appData);

            var paddle = sp.GetRequiredService<PaddleOcrV6Engine>();

            Assert.False(paddle.IsAvailable);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AddOcrEngines_MissingModels_FallsBackToMock()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(tempDir, "logs");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var _ = PinDevModelPathsDisabled();

            var appData = new TestAppDataProvider(tempDir, logDir);
            var sp = BuildServiceProvider(appData);

            var engine = sp.GetRequiredService<IOcrEngine>();

            Assert.Equal("MockOcr", engine.EngineName);
            Assert.IsType<MockOcrEngine>(engine);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
