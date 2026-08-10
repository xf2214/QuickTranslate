using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class WarmUpOnceTests
{
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

    [Fact]
    public async Task MockEngine_WarmUpMultipleTimes_NoSessionCreated()
    {
        var entries = new List<SpyLogEntry>();
        var logger = new SpyLogger<MockOcrEngine>(entries);
        var engine = new MockOcrEngine(logger);

        int sessionCreatedCount = 0;
        engine.SessionCreated += (_, _) => sessionCreatedCount++;

        await engine.WarmUpAsync();
        await engine.WarmUpAsync();
        await engine.WarmUpAsync();

        Assert.Equal(0, sessionCreatedCount);
    }

    [Fact]
    public async Task PaddleEngine_MissingModels_WarmUpMultipleTimes_NoSessionCreated()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(tempDir, "logs");
        Directory.CreateDirectory(tempDir);

        try
        {
            var appData = new TestAppDataProvider(tempDir, logDir);
            var settings = Options.Create(new AppSettings());
            var logger = Substitute.For<ILogger<PaddleOcrV6Engine>>();

            using var engine = new PaddleOcrV6Engine(settings, appData, logger);

            int sessionCreatedCount = 0;
            engine.SessionCreated += (_, _) => sessionCreatedCount++;

            await engine.WarmUpAsync();
            await engine.WarmUpAsync();
            await engine.WarmUpAsync();

            Assert.Equal(0, sessionCreatedCount);
            Assert.False(engine.IsAvailable);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
