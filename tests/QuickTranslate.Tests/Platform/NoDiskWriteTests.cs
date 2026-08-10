using System.Drawing;
using System.Drawing.Imaging;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;
using QuickTranslate.Core.Abstractions;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class NoDiskWriteTests
{
    private static readonly string[] ImageExts = { ".png", ".bmp", ".jpg", ".tmp" };

    private static HashSet<string> SnapshotImageFiles()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string dataDir = @"E:\翻译\.data";
        if (Directory.Exists(dataDir))
            AddFiles(set, dataDir);

        string appDir = AppContext.BaseDirectory;
        if (Directory.Exists(appDir))
            AddFiles(set, appDir);

        string tempPath = Path.GetTempPath();
        if (Directory.Exists(tempPath))
            AddFiles(set, tempPath);

        return set;
    }

    private static void AddFiles(HashSet<string> set, string dir)
    {
        try
        {
            foreach (var ext in ImageExts)
            {
                var files = Directory.GetFiles(dir, $"*{ext}", SearchOption.AllDirectories);
                foreach (var f in files)
                    set.Add(Path.GetFullPath(f));
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task CaptureRectAsync_100Times_NoNewImageFiles()
    {
        var before = SnapshotImageFiles();
        int beforeCount = before.Count;

        var monitorService = new MonitorService();
        var cursorService = new CursorService();
        var capture = new GdiScreenCapture(monitorService, cursorService);
        var region = new PhysicalRect(0, 0, 400, 300);

        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var frame = await capture.CaptureRectAsync(region);
            }
            catch
            {
            }
        }

        var after = SnapshotImageFiles();
        int afterCount = after.Count;

        Assert.Equal(beforeCount, afterCount);
        Assert.Subset(before, after);
    }
}
