using System.Diagnostics;
using System.IO;
using System.Text.Json;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Platform.Win32;

namespace QuickTranslate.Benchmarks;

public class EnvReport
{
    public string AppCommit { get; set; } = "unknown";
    public string ModelVersion { get; set; } = "MOCK";
    public string Cpu { get; set; } = "n/a";
    public int MemoryGB { get; set; }
    public string OSVersion { get; set; } = "";
    public uint Dpi { get; set; } = 96;
    public string Resolution { get; set; } = "";
    public string NetworkRegion { get; set; } = "local-only - offline mock";
    public long When { get; set; }

    public static EnvReport Collect(string repoRoot)
    {
        var report = new EnvReport();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --short HEAD",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    report.AppCommit = output;
                }
            }
        }
        catch
        {
        }

        var versionPath = Path.Combine(repoRoot, "assets", "models", "version.json");
        if (File.Exists(versionPath))
        {
            try
            {
                var json = File.ReadAllText(versionPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ppocr_version", out var ver))
                {
                    report.ModelVersion = ver.GetString() ?? "MOCK";
                }
            }
            catch
            {
            }
        }

        report.Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "n/a";

        try
        {
            report.MemoryGB = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30);
        }
        catch
        {
            report.MemoryGB = 0;
        }

        report.OSVersion = Environment.OSVersion.VersionString +
                           (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)");

        try
        {
            IMonitorService monitorService = new MonitorService();
            var monitors = monitorService.EnumerateMonitors();
            if (monitors != null && monitors.Count > 0)
            {
                var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
                report.Dpi = primary.DpiX;
                report.Resolution = $"{primary.Bounds.Width}x{primary.Bounds.Height}";
            }
            else
            {
                report.Dpi = 96;
                report.Resolution = "1920x1080 (fallback)";
            }
        }
        catch
        {
            report.Dpi = 96;
            report.Resolution = "1920x1080 (fallback)";
        }

        report.When = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return report;
    }
}
