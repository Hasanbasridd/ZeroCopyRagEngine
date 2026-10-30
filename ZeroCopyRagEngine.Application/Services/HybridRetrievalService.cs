using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Application.Services;

/// <summary>
/// Orchestrates Hybrid Search (SIMD Vector Search + BM25 Inverted Index + RRF + Optional Reranking).
/// </summary>
public sealed class HybridRetrievalService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorDatabase _vectorDatabase;
    private readonly IBm25Index? _bm25Index;
    private readonly IReranker? _reranker;

    public HybridRetrievalService(
        IEmbeddingService embeddingService,
        IVectorDatabase vectorDatabase,
        IBm25Index? bm25Index = null,
        IReranker? reranker = null)
    {
        _embeddingService = embeddingService;
        _vectorDatabase = vectorDatabase;
        _bm25Index = bm25Index;
        _reranker = reranker;
    }

    public async Task<IReadOnlyList<ScoredChunk>> RetrieveAsync(
        string query,
        int topK = 5,
        MetadataFilter? filter = null,
        int candidateMultiplier = 3,
        CancellationToken cancellationToken = default)
    {
        int candidatePoolSize = Math.Max(topK, topK * candidateMultiplier);
        float[] queryVector = await _embeddingService.GenerateEmbeddingAsync(query.AsMemory(), cancellationToken);

        var vectorCandidates = await _vectorDatabase.SearchSimilarAsync(
            queryVector.AsMemory(),
            candidatePoolSize,
            filter,
            cancellationToken);

        IReadOnlyList<ScoredChunk> fused;
        if (_bm25Index is not null)
        {
            var bm25Candidates = _bm25Index.Search(query.AsSpan(), candidatePoolSize, filter);
            fused = ReciprocalRankFusion.Fuse(vectorCandidates, bm25Candidates, candidatePoolSize);
        }
        else
        {
            fused = vectorCandidates;
        }

        IReadOnlyList<ScoredChunk> finalResults;
        if (_reranker is not null && fused.Count > 0)
        {
            finalResults = await _reranker.RerankAsync(query, fused, topK, cancellationToken);
        }
        else if (fused.Count > topK)
        {
            var trimmed = new List<ScoredChunk>(topK);
            for (int i = 0; i < topK; i++)
                trimmed.Add(fused[i]);
            finalResults = trimmed;
        }
        else
        {
            finalResults = fused;
        }

        return finalResults;
    }
}
