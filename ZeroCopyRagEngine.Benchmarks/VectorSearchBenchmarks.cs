using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ModernAiAssistant.Domain.Entities;
using ModernAiAssistant.Infrastructure.VectorStores;

namespace ModernAiAssistant.Benchmarks;

[MemoryDiagnoser]
public class VectorSearchBenchmarks
{
    private SimdLocalVectorDatabase _db = null!;
    private float[] _queryVector = null!;

    [GlobalSetup]
    public void Setup()
    {
        _db = new SimdLocalVectorDatabase();
        var random = new Random(42);
        int vectorCount = 100_000;
        int dimension = 384;
        
        var chunks = new TextChunk[vectorCount];
        var meta = new DocumentMetadata("f1", "f1.pdf", 1, 1);
        
        for (int i = 0; i < vectorCount; i++)
        {
            var vec = new float[dimension];
            for (int j = 0; j < dimension; j++) vec[j] = (float)random.NextDouble();
            chunks[i] = new TextChunk($"id_{i}", ReadOnlyMemory<char>.Empty, vec, meta);
        }

        _db.UpsertChunksAsync(chunks).GetAwaiter().GetResult();
        
        _queryVector = new float[dimension];
        for (int j = 0; j < dimension; j++) _queryVector[j] = (float)random.NextDouble();
    }

    [Benchmark]
    public async Task<int> SimdVectorSearch100k()
    {
        var results = await _db.SearchSimilarAsync(_queryVector.AsMemory(), topK: 5);
        return results.Count;
    }
}
