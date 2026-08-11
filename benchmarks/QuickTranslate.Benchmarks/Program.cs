using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickTranslate.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        string mode = "run";
        if (args.Length > 0)
        {
            mode = args[0].ToLowerInvariant() switch
            {
                "run" => "run",
                "fast" => "fast",
                _ => "run"
            };
        }

        bool fastMode = mode == "fast";
        string repoRoot = FindRepoRoot();
        string resultsDir = Path.Combine(repoRoot, "docs", "benchmark-results");
        Directory.CreateDirectory(resultsDir);

        Console.WriteLine($"=== QuickTranslate Benchmarks ===");
        Console.WriteLine($"Mode: {mode} (fast={fastMode})");
        Console.WriteLine($"Repo: {repoRoot}");
        Console.WriteLine($"Results: {resultsDir}");
        Console.WriteLine();

        var overallSw = Stopwatch.StartNew();

        Console.WriteLine("[1/4] Collecting environment info...");
        var env = EnvReport.Collect(repoRoot);
        Console.WriteLine($"  Commit: {env.AppCommit}");
        Console.WriteLine($"  CPU: {env.Cpu}");
        Console.WriteLine($"  Memory: {env.MemoryGB} GB");
        Console.WriteLine($"  DPI: {env.Dpi}, Resolution: {env.Resolution}");
        Console.WriteLine();

        Console.WriteLine("[2/4] Running benchmarks...");
        var runner = new BenchmarksRunner(fastMode, repoRoot);
        var metrics = runner.RunAll();

        Console.WriteLine($"  {metrics.Count} metric groups collected:");
        foreach (var m in metrics)
        {
            Console.WriteLine($"    - {m.Name}: P50={FormatNumber(m.P50)}{m.Unit}, P95={FormatNumber(m.P95)}{m.Unit} (N={m.SampleCount})");
        }
        Console.WriteLine();

        Console.WriteLine("[3/4] Writing output files...");
        var report = new BenchmarkReport
        {
            Env = env,
            Metrics = metrics,
            Mode = mode,
            DurationMs = overallSw.ElapsedMilliseconds
        };

        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string safeCommit = string.IsNullOrEmpty(env.AppCommit) || env.AppCommit == "unknown"
            ? "unknown"
            : env.AppCommit;
        string baseName = $"{safeCommit}-{timestamp}";

        string mdPath = Path.Combine(resultsDir, $"{baseName}.md");
        string jsonPath = Path.Combine(resultsDir, $"{baseName}.json");

        WriteMarkdown(mdPath, report);
        WriteJson(jsonPath, report);

        Console.WriteLine($"  Markdown: {mdPath}");
        Console.WriteLine($"  JSON:     {jsonPath}");
        Console.WriteLine();

        overallSw.Stop();
        Console.WriteLine($"[4/4] Done. Total duration: {overallSw.ElapsedMilliseconds}ms");
        Console.WriteLine("Exit code: 0");
        return 0;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "QuickTranslate.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string FormatNumber(double v)
    {
        if (Math.Abs(v) >= 1000)
            return v.ToString("F0");
        if (Math.Abs(v) >= 100)
            return v.ToString("F1");
        if (Math.Abs(v) >= 1)
            return v.ToString("F2");
        return v.ToString("F4");
    }

    private static void WriteMarkdown(string path, BenchmarkReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# QuickTranslate Performance Benchmark Report");
        sb.AppendLine();
        sb.AppendLine($"_Generated: {DateTimeOffset.FromUnixTimeMilliseconds(report.Env.When):yyyy-MM-dd HH:mm:ss UTC}_");
        sb.AppendLine($"_Mode: **{report.Mode}** | Total duration: {report.DurationMs}ms_");
        sb.AppendLine();

        sb.AppendLine("## Environment Metadata");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| AppCommit | `{report.Env.AppCommit}` |");
        sb.AppendLine($"| ModelVersion | `{report.Env.ModelVersion}` |");
        sb.AppendLine($"| Cpu | {report.Env.Cpu} |");
        sb.AppendLine($"| MemoryGB | {report.Env.MemoryGB} |");
        sb.AppendLine($"| OSVersion | {report.Env.OSVersion} |");
        sb.AppendLine($"| Dpi | {report.Env.Dpi} |");
        sb.AppendLine($"| Resolution | {report.Env.Resolution} |");
        sb.AppendLine($"| NetworkRegion | {report.Env.NetworkRegion} |");
        sb.AppendLine($"| When (unix ms) | {report.Env.When} |");
        sb.AppendLine();

        sb.AppendLine("## Metrics (P50 / P95)");
        sb.AppendLine();
        sb.AppendLine("| Metric | Unit | P50 | P95 | N samples | Notes |");
        sb.AppendLine("|---|---|---:|---:|---:|---|");
        foreach (var m in report.Metrics)
        {
            sb.AppendLine($"| {m.Name} | {m.Unit} | {FormatNumber(m.P50)} | {FormatNumber(m.P95)} | {m.SampleCount} | {m.Notes} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Notes");
        sb.AppendLine();
        sb.AppendLine("- 手工采集（Mock OCR/Network），TTFT 不适用");
        sb.AppendLine("- OCR 阶段使用 Mock 固定 100ms Task.Delay（BenchFakeOcrEngine），不涉及真实模型推理");
        sb.AppendLine("- 翻译阶段使用 StubTranslationRouter 同步返回，不涉及网络请求");
        sb.AppendLine("- P50/P95 基于各样本的排序百分位计算（Math.Ceiling(p/100*N) - 1）");
        sb.AppendLine("- Idle 指标：采样期间无用户活动，进程工作集与 CPU 占比（Nproc 归一化）");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteJson(string path, BenchmarkReport report)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(report, options);
        File.WriteAllText(path, json, Encoding.UTF8);
    }
}
