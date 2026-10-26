using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime.Tensors;
using QuickTranslate.Infrastructure.AppData;
using QuickTranslate.Infrastructure.Cache;
using QuickTranslate.Infrastructure.Ocr;

namespace QuickTranslate.HotPathBenchmarks;

internal static class Program
{
    private static int _sink;

    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet run --project benchmarks/QuickTranslate.HotPathBenchmarks -c Release -- <ecdict-lite.packed>");
            return 2;
        }

        var path = Path.GetFullPath(args[0]);
        if (!string.Equals(Path.GetFileName(path), "ecdict-lite.packed", StringComparison.Ordinal) ||
            File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "custom-dictionary.txt")))
        {
            Console.Error.WriteLine("Use a file named ecdict-lite.packed in a directory without custom-dictionary.txt so the measured dictionary matches the reported asset.");
            return 2;
        }
        // Hashing deliberately warms the filesystem cache. Load time below is NOT
        // a cold-disk/startup benchmark; it isolates parsing and index creation.
        using var file = File.OpenRead(path);
        var dictionaryHash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        var data = new BenchmarkAppData(Path.GetDirectoryName(path)!);
        var loadSamples = new List<Sample>();
        EcdictLiteDictionary dictionary = null!;
        for (var sample = 0; sample < 5; sample++)
        {
            dictionary = null!;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            dictionary = new EcdictLiteDictionary(data, NullLogger<EcdictLiteDictionary>.Instance);
            loadSamples.Add(Measure(() => _sink = dictionary.Count));
        }

        var vocabulary = new[] { "hello", "world", "function", "translation", "performance", "not-a-real-word-qt-2026" };
        const int lookupsPerSample = 10_000;
        Action lookupResults = () =>
        {
            for (var i = 0; i < lookupsPerSample; i++)
                if (dictionary.TryLookup(vocabulary[i % vocabulary.Length], "zh", out _)) _sink++;
        };

        // Reflection is outside the measurements and permits running this same
        // harness against baseline assemblies that predate the membership API.
        var containsMethod = dictionary.GetType().GetMethod("ContainsWord", new[] { typeof(string) });
        var contains = containsMethod?.CreateDelegate<Func<string, bool>>(dictionary);
        Action? membership = contains is null ? null : () =>
        {
            for (var i = 0; i < lookupsPerSample; i++)
                if (contains(vocabulary[i % vocabulary.Length])) _sink++;
        };

        const int steps = 320;
        const int classes = 18_710;
        var values = new float[steps * classes];
        var labels = new string[classes];
        Array.Fill(labels, "a");
        labels[0] = "";
        labels[^1] = " ";
        for (var step = 0; step < steps; step++)
            values[step * classes + (step % 2)] = 1f;
        var tensor = new DenseTensor<float>(values, new[] { 1, steps, classes });

        Action copiedDecode = () =>
        {
            var result = CtcGreedyDecoder.Decode(tensor.ToArray(), steps, classes, labels);
            _sink += result.Text.Length;
        };
        Action directDecode = () =>
        {
            var result = CtcGreedyDecoder.DecodeTensor(tensor, labels);
            _sink += result.Text.Length;
        };

        var metrics = new List<Metric>
        {
            Summarize("dictionary_load_warm_filesystem", loadSamples, 1),
            Run("dictionary_lookup_with_result", lookupResults, 25, lookupsPerSample),
        };
        if (membership != null) metrics.Add(Run("dictionary_membership", membership, 25, lookupsPerSample));
        metrics.Add(Run("ctc_copy_then_decode", copiedDecode, 31, 1));
        metrics.Add(Run("ctc_direct_tensor_decode", directDecode, 31, 1));

        using var infrastructureFile = File.OpenRead(typeof(EcdictLiteDictionary).Assembly.Location);
        var infrastructureSha256 = Convert.ToHexString(SHA256.HashData(infrastructureFile)).ToLowerInvariant();
        var report = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            infrastructureVersion = typeof(EcdictLiteDictionary).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            infrastructureSha256,
            dictionaryBytes = new FileInfo(path).Length,
            dictionarySha256 = dictionaryHash,
            dictionaryEntries = dictionary.Count,
            tensorShape = new[] { 1, steps, classes },
            tensorBytes = (long)steps * classes * sizeof(float),
            metrics,
            notes = new[]
            {
                "CPU microbenchmarks only: no ONNX inference, screen capture, UI or network.",
                "CTC comparison holds decoding constant and isolates the removed output copy.",
                "Dictionary load follows a file hash and includes parsing/index construction, not cold disk I/O.",
                "P50/P95 use nearest-rank percentiles; bytes are managed allocations on the executing thread.",
                "No measurements imply an end-to-end OCR speedup or recognition-accuracy change."
            }
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(_sink);
        return 0;
    }

    private static Sample Measure(Action action)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        action();
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new Sample(elapsed, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    private static Metric Run(string name, Action action, int samples, int operations)
    {
        for (var warmup = 0; warmup < 8; warmup++) action();
        var results = new List<Sample>(samples);
        for (var i = 0; i < samples; i++) results.Add(Measure(action));
        return Summarize(name, results, operations);
    }

    private static Metric Summarize(string name, List<Sample> samples, int operations)
    {
        var elapsed = samples.Select(s => s.ElapsedMs / operations).Order().ToArray();
        return new Metric(name, samples.Count, operations,
            elapsed[(int)Math.Ceiling(elapsed.Length * 0.5) - 1],
            elapsed[(int)Math.Ceiling(elapsed.Length * 0.95) - 1],
            samples.Average(s => (double)s.AllocatedBytes / operations));
    }

    private sealed record Sample(double ElapsedMs, long AllocatedBytes);
    private sealed record Metric(string Name, int Samples, int OperationsPerSample,
        double P50MillisecondsPerOperation, double P95MillisecondsPerOperation, double AllocatedBytesPerOperation);
    private sealed class BenchmarkAppData(string directory) : IAppDataProvider
    {
        public string GetAppDataDirectory() => directory;
        public string GetLogDirectory() => directory;
    }
}
