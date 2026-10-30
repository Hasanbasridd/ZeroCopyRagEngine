using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Infrastructure.VectorStores;

namespace ZeroCopyRagEngine.Benchmarks;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--measure", StringComparison.OrdinalIgnoreCase))
        {
            await RunHardwareVerifiedBenchmarksAsync();
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    private static async Task RunHardwareVerifiedBenchmarksAsync()
    {
        Console.WriteLine("# Hardware & Runtime Environment");
        Console.WriteLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"- .NET Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"- Processor Count: {Environment.ProcessorCount} logical cores");
        Console.WriteLine($"- Vector<float>.Count: {Vector<float>.Count} floats ({Vector<float>.Count * 32}-bit SIMD)");
        Console.WriteLine($"- AVX2 Supported: {Avx2.IsSupported}, AVX-512F Supported: {Avx512F.IsSupported}, ARM NEON Supported: {AdvSimd.IsSupported}");
        Console.WriteLine();

        var chunkBench = new ChunkerBenchmarks();
        chunkBench.Setup();
        chunkBench.TraditionalStringSplit();
        chunkBench.NaiveSpanSubstringChunker();
        chunkBench.ZeroCopySpanChunking();
        chunkBench.ZeroCopySpanDestinationBuffer();

        Console.WriteLine("## 1. Chunking Benchmarks (50 Iterations)");
        Console.WriteLine("| Method | Mean (ms) | StdDev (ms) | Allocated / Op |");
        Console.WriteLine("| :--- | :---: | :---: | :---: |");
        MeasureOperation("TraditionalStringSplit (Baseline)", () => chunkBench.TraditionalStringSplit(), 50);
        MeasureOperation("NaiveSpanSubstringChunker", () => chunkBench.NaiveSpanSubstringChunker(), 50);
        MeasureOperation("ZeroCopySpanChunking (IEnumerable)", () => chunkBench.ZeroCopySpanChunking(), 50);
        MeasureOperation("ZeroCopySpanDestinationBuffer (Span)", () => chunkBench.ZeroCopySpanDestinationBuffer(), 50);
        Console.WriteLine();

        Console.WriteLine("## 2. Vector Search Benchmarks (Dimension=384, TopK=10)");
        Console.WriteLine("| Store / Mode | Vectors (N) | Mean (ms) | StdDev (ms) | Effective Bandwidth (GB/s) | Allocated / Op | Recall@10 |");
        Console.WriteLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |");

        foreach (int n in new[] { 10_000, 100_000 })
        {
            var flatDb = new SimdLocalVectorDatabase();
            var random = new Random(42);
            int dim = 384;
            var chunks = new TextChunk[n];
            var meta = new DocumentMetadata("f1", "f1.pdf", 1, 1);

            for (int i = 0; i < n; i++)
            {
                var vec = new float[dim];
                for (int j = 0; j < dim; j++) vec[j] = (float)(random.NextDouble() * 2.0 - 1.0);
                chunks[i] = new TextChunk($"id_{i}", ReadOnlyMemory<char>.Empty, vec, meta);
            }

            await flatDb.UpsertChunksAsync(chunks);

            var query = new float[dim];
            for (int j = 0; j < dim; j++) query[j] = (float)(random.NextDouble() * 2.0 - 1.0);
            var topIdx = new int[10];
            var topScores = new float[10];

            double bytesScannedFlat = (double)n * dim * sizeof(float);
            MeasureVectorOp("SimdFlatSearch (SearchSimilarSpan)", n, bytesScannedFlat, 1.000, () => flatDb.SearchSimilarSpan(query, topIdx, topScores), 30);
            MeasureVectorOp("SimdFlatSearch (SearchSimilarAsync)", n, bytesScannedFlat, 1.000, () => flatDb.SearchSimilarAsync(query, 10).GetAwaiter().GetResult().Count, 30);
        }
    }

    private static void MeasureOperation(string name, Func<int> action, int iterations)
    {
        action();
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        action();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        long allocPerOp = afterAlloc - beforeAlloc;

        double[] samples = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        var (mean, stdDev) = ComputeStats(samples);
        Console.WriteLine($"| `{name}` | {mean:F3} ms | ±{stdDev:F3} ms | {FormatBytes(allocPerOp)} |");
    }

    private static void MeasureVectorOp(string name, int n, double bytesScanned, double recall, Func<int> action, int iterations)
    {
        action();
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        action();
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        long allocPerOp = afterAlloc - beforeAlloc;

        double[] samples = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        var (mean, stdDev) = ComputeStats(samples);
        string bw = bytesScanned > 0 ? $"{(bytesScanned / 1e9) / (mean / 1000.0):F2} GB/s" : "Graph O(log N)";
        Console.WriteLine($"| `{name}` | {n:N0} | {mean:F3} ms | ±{stdDev:F3} ms | {bw} | {FormatBytes(allocPerOp)} | {recall:F3} |");
    }

    private static (double Mean, double StdDev) ComputeStats(double[] values)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        double mean = sum / values.Length;

        double sqDiff = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            sqDiff += d * d;
        }
        double stdDev = Math.Sqrt(sqDiff / Math.Max(1, values.Length - 1));
        return (mean, stdDev);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
