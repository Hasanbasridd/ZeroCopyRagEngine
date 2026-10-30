using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;
using ZeroCopyRagEngine.Infrastructure.Chunkers;
using ZeroCopyRagEngine.Infrastructure.Embedding;
using ZeroCopyRagEngine.Infrastructure.Evaluation;
using ZeroCopyRagEngine.Infrastructure.VectorStores;

namespace ZeroCopyRagEngine.Benchmarks;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--eval", StringComparison.OrdinalIgnoreCase))
        {
            string datasetPath = args.Length > 1 ? args[1] : FindDatasetPath();
            return await RunEvaluationSuiteAsync(datasetPath);
        }

        if (args.Length > 0 && string.Equals(args[0], "--measure", StringComparison.OrdinalIgnoreCase))
        {
            await RunHardwareVerifiedBenchmarksAsync();
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    private static string FindDatasetPath()
    {
        string[] candidates =
        {
            Path.Combine(Directory.GetCurrentDirectory(), "datasets", "golden-dataset.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "datasets", "golden-dataset.json")
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
                return Path.GetFullPath(candidates[i]);
        }

        throw new FileNotFoundException("Could not locate datasets/golden-dataset.json.");
    }

    private static async Task<int> RunEvaluationSuiteAsync(string datasetPath)
    {
        Console.WriteLine($"# ZeroCopyRagEngine — Golden Dataset Evaluation Report");
        Console.WriteLine($"Dataset: {datasetPath}");
        var dataset = RagEvaluator.LoadGoldenDataset(datasetPath);
        var embedding = new DummyEmbeddingService();

        var reports = new List<EvaluationReport>();

        // 1. Span Semantic Chunker + Flat SIMD Float32
        reports.Add(await RagEvaluator.EvaluateConfigurationAsync(
            "SpanOverlapChunker + Flat Float32 SIMD",
            dataset,
            new ZeroCopySemanticOverlapChunker(),
            new SimdLocalVectorDatabase(),
            embedding,
            useHybridBm25: false,
            useReranker: false,
            k: 5));

        // 2. Token-Aware Chunker + Flat SIMD Float32
        reports.Add(await RagEvaluator.EvaluateConfigurationAsync(
            "TokenAwareChunker (ML.Tokenizers) + Flat Float32 SIMD",
            dataset,
            new TokenAwareZeroCopyChunker(),
            new SimdLocalVectorDatabase(),
            embedding,
            useHybridBm25: false,
            useReranker: false,
            k: 5));

        // 3. Markdown Hierarchy Chunker + Flat SIMD Float32
        reports.Add(await RagEvaluator.EvaluateConfigurationAsync(
            "MarkdownHierarchyChunker + Flat Float32 SIMD",
            dataset,
            new MarkdownHierarchyZeroCopyChunker(),
            new SimdLocalVectorDatabase(),
            embedding,
            useHybridBm25: false,
            useReranker: false,
            k: 5));

        // 4. Memory-Mapped Zero-Copy Vector Store
        string tempMmap = Path.Combine(Path.GetTempPath(), $"zcr_eval_{Guid.NewGuid():N}.vec");
        try
        {
            using var mmapDb = new MemoryMappedVectorDatabase(tempMmap);
            reports.Add(await RagEvaluator.EvaluateConfigurationAsync(
                "SpanOverlapChunker + MemoryMappedFile Zero-Copy",
                dataset,
                new ZeroCopySemanticOverlapChunker(),
                mmapDb,
                embedding,
                useHybridBm25: false,
                useReranker: false,
                k: 5));
        }
        finally
        {
            if (File.Exists(tempMmap)) File.Delete(tempMmap);
            if (File.Exists(tempMmap + ".meta")) File.Delete(tempMmap + ".meta");
        }

        // 5. Int8 Scalar Quantized Vector Store
        reports.Add(await RagEvaluator.EvaluateConfigurationAsync(
            "SpanOverlapChunker + Int8 Quantized (4x Compressed)",
            dataset,
            new ZeroCopySemanticOverlapChunker(),
            new Int8QuantizedVectorDatabase(),
            embedding,
            useHybridBm25: false,
            useReranker: false,
            k: 5));

        // 6. HNSW Approximate Nearest Neighbor Graph
        reports.Add(await RagEvaluator.EvaluateConfigurationAsync(
            "SpanOverlapChunker + HNSW Graph (M=16, efSearch=48)",
            dataset,
            new ZeroCopySemanticOverlapChunker(),
            new HnswVectorDatabase(m: 16, efConstruction: 64, efSearch: 48),
            embedding,
            useHybridBm25: false,
            useReranker: false,
            k: 5));

        // 7. Hybrid Search (SIMD Float32 + BM25 + RRF + CrossEncoder Reranker)
        var hybridReport = await RagEvaluator.EvaluateConfigurationAsync(
            "MarkdownHierarchy + Hybrid (SIMD + BM25 + RRF + Reranker)",
            dataset,
            new MarkdownHierarchyZeroCopyChunker(),
            new SimdLocalVectorDatabase(),
            embedding,
            useHybridBm25: true,
            useReranker: true,
            k: 5);
        reports.Add(hybridReport);

        Console.WriteLine();
        Console.WriteLine("| Configuration | Queries | Recall@5 | MRR@5 | nDCG@5 | Faithfulness | Answer Relevance | Avg Latency (ms) |");
        Console.WriteLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");
        foreach (var r in reports)
        {
            Console.WriteLine(
                $"| `{r.ConfigurationName}` | {r.QuestionCount} | {r.RecallAtK:F3} | {r.MrrAtK:F3} | {r.NdcgAtK:F3} | {r.Faithfulness:F3} | {r.AnswerRelevance:F3} | {r.AverageLatencyMs:F2} ms |");
        }

        // CI Regression Gate (Madde 8.5)
        if (hybridReport.RecallAtK < 0.85 || hybridReport.MrrAtK < 0.75 || hybridReport.NdcgAtK < 0.80)
        {
            Console.Error.WriteLine(
                $"🚨 EVALUATION REGRESSION DETECTED: Hybrid Recall@5={hybridReport.RecallAtK:F3}, MRR@5={hybridReport.MrrAtK:F3}, nDCG@5={hybridReport.NdcgAtK:F3}");
            return 1;
        }

        Console.WriteLine("\n✅ All RAG Evaluation quality thresholds passed.");
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

        // 1. Chunking Benchmarks
        var chunkBench = new ChunkerBenchmarks();
        chunkBench.Setup();

        // Warmup
        chunkBench.TraditionalStringSplit();
        chunkBench.NaiveSpanSubstringChunker();
        chunkBench.MicrosoftMlTokenizerChunker();
        chunkBench.ZeroCopySpanChunking();
        chunkBench.ZeroCopySpanDestinationBuffer();
        chunkBench.MarkdownHierarchyZeroCopyChunking();

        Console.WriteLine("## 1. Chunking Benchmarks (50 Iterations)");
        Console.WriteLine("| Method | Mean (ms) | StdDev (ms) | Allocated / Op |");
        Console.WriteLine("| :--- | :---: | :---: | :---: |");
        MeasureOperation("TraditionalStringSplit (Baseline)", () => chunkBench.TraditionalStringSplit(), 50);
        MeasureOperation("NaiveSpanSubstringChunker", () => chunkBench.NaiveSpanSubstringChunker(), 50);
        MeasureOperation("MicrosoftMlTokenizerChunker", () => chunkBench.MicrosoftMlTokenizerChunker(), 20);
        MeasureOperation("ZeroCopySpanChunking (IEnumerable)", () => chunkBench.ZeroCopySpanChunking(), 50);
        MeasureOperation("ZeroCopySpanDestinationBuffer (Span)", () => chunkBench.ZeroCopySpanDestinationBuffer(), 50);
        MeasureOperation("MarkdownHierarchyZeroCopyChunking", () => chunkBench.MarkdownHierarchyZeroCopyChunking(), 50);
        Console.WriteLine();

        // 2. Vector Search Benchmarks (Flat vs MMap vs Int8 vs HNSW + Recall@10)
        Console.WriteLine("## 2. Vector Search Benchmarks (Dimension=384, TopK=10)");
        Console.WriteLine("| Store / Mode | Vectors (N) | Mean (ms) | StdDev (ms) | Effective Bandwidth (GB/s) | Allocated / Op | Recall@10 |");
        Console.WriteLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |");

        foreach (int n in new[] { 10_000, 100_000 })
        {
            var flatDb = new SimdLocalVectorDatabase();
            var int8Db = new Int8QuantizedVectorDatabase();
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
            await int8Db.UpsertChunksAsync(chunks);

            var query = new float[dim];
            for (int j = 0; j < dim; j++) query[j] = (float)(random.NextDouble() * 2.0 - 1.0);
            var topIdx = new int[10];
            var topScores = new float[10];

            var exactTop10 = await flatDb.SearchSimilarAsync(query, 10);
            var int8Top10 = await int8Db.SearchSimilarAsync(query, 10);
            double int8Recall = Int8QuantizedVectorDatabase.CalculateRecallAtK(exactTop10, int8Top10, 10);

            double bytesScannedFlat = (double)n * dim * sizeof(float);
            MeasureVectorOp("SimdFlatSearch (SearchSimilarSpan)", n, bytesScannedFlat, 1.000, () => flatDb.SearchSimilarSpan(query, topIdx, topScores), 30);
            MeasureVectorOp("SimdFlatSearch (SearchSimilarAsync)", n, bytesScannedFlat, 1.000, () => flatDb.SearchSimilarAsync(query, 10).GetAwaiter().GetResult().Count, 30);

            string mmapPath = Path.Combine(Path.GetTempPath(), $"zcr_m_{n}.vec");
            try
            {
                using var mmapDb = new MemoryMappedVectorDatabase(mmapPath);
                await mmapDb.UpsertChunksAsync(chunks);
                var mmapTop10 = await mmapDb.SearchSimilarAsync(query, 10);
                double mmapRecall = Int8QuantizedVectorDatabase.CalculateRecallAtK(exactTop10, mmapTop10, 10);
                MeasureVectorOp("MemoryMappedVectorDatabase (OS Page Cache)", n, bytesScannedFlat, mmapRecall, () => mmapDb.SearchSimilarAsync(query, 10).GetAwaiter().GetResult().Count, 30);
            }
            finally
            {
                if (File.Exists(mmapPath)) File.Delete(mmapPath);
                if (File.Exists(mmapPath + ".meta")) File.Delete(mmapPath + ".meta");
            }

            double bytesScannedInt8 = (double)n * dim * sizeof(sbyte);
            MeasureVectorOp("Int8QuantizedVectorDatabase (4x Compressed)", n, bytesScannedInt8, int8Recall, () => int8Db.SearchSimilarAsync(query, 10).GetAwaiter().GetResult().Count, 30);

            if (n <= 10_000)
            {
                var hnswDb = new HnswVectorDatabase(m: 16, efConstruction: 64, efSearch: 64);
                await hnswDb.UpsertChunksAsync(chunks);
                var hnswTop10 = await hnswDb.SearchSimilarAsync(query, 10);
                double hnswRecall = Int8QuantizedVectorDatabase.CalculateRecallAtK(exactTop10, hnswTop10, 10);
                MeasureVectorOp("HnswVectorDatabase (M=16, efSearch=64)", n, 0, hnswRecall, () => hnswDb.SearchSimilarAsync(query, 10).GetAwaiter().GetResult().Count, 50);
            }
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
