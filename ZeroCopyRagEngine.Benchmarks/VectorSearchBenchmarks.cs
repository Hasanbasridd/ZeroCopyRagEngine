using System;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Infrastructure.VectorStores;

namespace ZeroCopyRagEngine.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class VectorSearchBenchmarks
{
    private SimdLocalVectorDatabase _flatDb = null!;
    private Int8QuantizedVectorDatabase _int8Db = null!;
    private HnswVectorDatabase _hnswDb = null!;
    private MemoryMappedVectorDatabase? _mmapDb;
    private string _tempMmapPath = string.Empty;

    private float[] _queryVector = null!;
    private int[] _topIndicesBuffer = null!;
    private float[] _topScoresBuffer = null!;

    [Params(384, 768, 1536)]
    public int Dimension { get; set; } = 384;

    [Params(10_000, 100_000, 1_000_000)]
    public int VectorCount { get; set; } = 10_000;

    [Params(5, 10)]
    public int TopK { get; set; } = 5;

    [GlobalSetup]
    public void Setup()
    {
        _flatDb = new SimdLocalVectorDatabase();
        _int8Db = new Int8QuantizedVectorDatabase();
        _hnswDb = new HnswVectorDatabase(m: 16, efConstruction: 48, efSearch: 32);

        var random = new Random(42);
        var chunks = new TextChunk[VectorCount];
        var meta = new DocumentMetadata("f1", "f1.pdf", 1, 1);

        for (int i = 0; i < VectorCount; i++)
        {
            var vec = new float[Dimension];
            for (int j = 0; j < Dimension; j++)
                vec[j] = (float)(random.NextDouble() * 2.0 - 1.0);

            chunks[i] = new TextChunk($"id_{i}", ReadOnlyMemory<char>.Empty, vec, meta);
        }

        _flatDb.UpsertChunksAsync(chunks).GetAwaiter().GetResult();
        _int8Db.UpsertChunksAsync(chunks).GetAwaiter().GetResult();

        // Build HNSW and MemoryMappedFile for moderate dataset sizes to keep benchmark setup bounded
        if (VectorCount <= 100_000)
        {
            _tempMmapPath = Path.Combine(Path.GetTempPath(), $"zcr_bench_{Dimension}_{VectorCount}.vec");
            if (File.Exists(_tempMmapPath)) File.Delete(_tempMmapPath);
            if (File.Exists(_tempMmapPath + ".meta")) File.Delete(_tempMmapPath + ".meta");

            _mmapDb = new MemoryMappedVectorDatabase(_tempMmapPath);
            _mmapDb.UpsertChunksAsync(chunks).GetAwaiter().GetResult();
        }

        if (VectorCount <= 10_000)
        {
            _hnswDb.UpsertChunksAsync(chunks).GetAwaiter().GetResult();
        }

        _queryVector = new float[Dimension];
        for (int j = 0; j < Dimension; j++)
            _queryVector[j] = (float)(random.NextDouble() * 2.0 - 1.0);

        _topIndicesBuffer = new int[TopK];
        _topScoresBuffer = new float[TopK];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mmapDb?.Dispose();
        if (!string.IsNullOrEmpty(_tempMmapPath))
        {
            if (File.Exists(_tempMmapPath)) File.Delete(_tempMmapPath);
            if (File.Exists(_tempMmapPath + ".meta")) File.Delete(_tempMmapPath + ".meta");
        }
    }

    [Benchmark(Baseline = true, Description = "SimdLocalVectorDatabase.SearchSimilarAsync (Float32 Flat)")]
    public async Task<int> SimdVectorSearchAsync()
    {
        var results = await _flatDb.SearchSimilarAsync(_queryVector.AsMemory(), TopK);
        return results.Count;
    }

    [Benchmark(Description = "SimdLocalVectorDatabase.SearchSimilarSpan (Float32 Flat, 0 B)")]
    public int SimdVectorSearchZeroAllocSpan()
    {
        return _flatDb.SearchSimilarSpan(_queryVector.AsSpan(), _topIndicesBuffer.AsSpan(), _topScoresBuffer.AsSpan());
    }

    [Benchmark(Description = "Int8QuantizedVectorDatabase.SearchSimilarAsync (4x Compressed)")]
    public async Task<int> Int8QuantizedSearchAsync()
    {
        var results = await _int8Db.SearchSimilarAsync(_queryVector.AsMemory(), TopK);
        return results.Count;
    }

    [Benchmark(Description = "HnswVectorDatabase.SearchSimilarAsync (ANN Graph)")]
    public async Task<int> HnswGraphSearchAsync()
    {
        if (_hnswDb.Count == 0) return 0;
        var results = await _hnswDb.SearchSimilarAsync(_queryVector.AsMemory(), TopK);
        return results.Count;
    }
}
