using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ZeroCopyRagEngine.Application.Services;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Infrastructure.BM25;
using ZeroCopyRagEngine.Infrastructure.Embedding;
using ZeroCopyRagEngine.Infrastructure.Reranking;
using ZeroCopyRagEngine.Infrastructure.VectorStores;

namespace ZeroCopyRagEngine.Benchmarks;

[MemoryDiagnoser]
public class HybridSearchBenchmarks
{
    private SimdLocalVectorDatabase _vectorDb = null!;
    private Bm25InvertedIndex _bm25 = null!;
    private LexicalCrossEncoderReranker _reranker = null!;
    private HybridRetrievalService _hybridService = null!;
    private IReadOnlyList<ScoredChunk> _vectorCandidates = null!;
    private IReadOnlyList<ScoredChunk> _bm25Candidates = null!;

    [GlobalSetup]
    public void Setup()
    {
        var embedding = new DummyEmbeddingService();
        _vectorDb = new SimdLocalVectorDatabase();
        _bm25 = new Bm25InvertedIndex();
        _reranker = new LexicalCrossEncoderReranker();

        var chunks = new List<TextChunk>(1000);
        for (int i = 0; i < 1000; i++)
        {
            string text = $"Document chunk {i} discussing zero-copy span arraypool simd vector cosine similarity hybrid bm25 search and hnsw index.";
            float[] vec = embedding.GenerateEmbeddingAsync(text.AsMemory()).GetAwaiter().GetResult();
            var meta = new DocumentMetadata($"doc_{i}", $"doc_{i}.md", 1, i, "benchmarks", DateTimeOffset.UtcNow, new[] { "rag", "simd" });
            chunks.Add(new TextChunk($"id_{i}", text.AsMemory(), vec, meta));
        }

        _vectorDb.UpsertChunksAsync(chunks).GetAwaiter().GetResult();
        _bm25.IndexChunks(chunks);
        _hybridService = new HybridRetrievalService(embedding, _vectorDb, _bm25, _reranker);

        float[] qVec = embedding.GenerateEmbeddingAsync("simd vector cosine similarity search".AsMemory()).GetAwaiter().GetResult();
        _vectorCandidates = _vectorDb.SearchSimilarAsync(qVec, 15).GetAwaiter().GetResult();
        _bm25Candidates = _bm25.Search("simd vector cosine similarity search".AsSpan(), 15);
    }

    [Benchmark(Description = "BM25 Inverted Index Search (1k docs)")]
    public int Bm25LexicalSearch()
    {
        return _bm25.Search("simd vector cosine similarity search".AsSpan(), 10).Count;
    }

    [Benchmark(Description = "Reciprocal Rank Fusion (RRF k=60)")]
    public int ReciprocalRankFusionMerge()
    {
        return ReciprocalRankFusion.Fuse(_vectorCandidates, _bm25Candidates, topK: 10).Count;
    }

    [Benchmark(Description = "Full Hybrid Search (SIMD + BM25 + RRF + Reranker)")]
    public async Task<int> FullHybridRetrieveAsync()
    {
        var res = await _hybridService.RetrieveAsync("simd vector cosine similarity search", topK: 5);
        return res.Count;
    }
}

