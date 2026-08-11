namespace QuickTranslate.Benchmarks;

public class BenchmarkMetric
{
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public double P50 { get; set; }
    public double P95 { get; set; }
    public int SampleCount { get; set; }
    public string Notes { get; set; } = "";
    public List<double> Samples { get; set; } = new();
}

public class BenchmarkReport
{
    public EnvReport Env { get; set; } = new();
    public List<BenchmarkMetric> Metrics { get; set; } = new();
    public string Mode { get; set; } = "run";
    public long DurationMs { get; set; }
}
