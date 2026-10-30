using System;
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

        _queryVector = new float[Dimension];
        for (int j = 0; j < Dimension; j++)
            _queryVector[j] = (float)(random.NextDouble() * 2.0 - 1.0);

        _topIndicesBuffer = new int[TopK];
        _topScoresBuffer = new float[TopK];
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
}
