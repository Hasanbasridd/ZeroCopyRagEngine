using System;
using System.Collections.Generic;
using ZeroCopyRagEngine.Domain.Entities;

namespace ZeroCopyRagEngine.Application.Services;

/// <summary>
/// Combines dense vector search and sparse lexical (BM25) search results using Reciprocal Rank Fusion (RRF).
/// Formula: RRF_Score(d) = sum_{r in rankings} 1 / (k + rank_r(d))
/// </summary>
public static class ReciprocalRankFusion
{
    public static IReadOnlyList<ScoredChunk> Fuse(
        IReadOnlyList<ScoredChunk> vectorResults,
        IReadOnlyList<ScoredChunk> lexicalResults,
        int topK,
        int kConstant = 60)
    {
        if (topK <= 0)
            return Array.Empty<ScoredChunk>();

        if (lexicalResults.Count == 0)
        {
            if (vectorResults.Count <= topK)
                return vectorResults;

            var trimmed = new List<ScoredChunk>(topK);
            for (int i = 0; i < topK; i++)
                trimmed.Add(vectorResults[i]);
            return trimmed;
        }

        if (vectorResults.Count == 0)
        {
            if (lexicalResults.Count <= topK)
                return lexicalResults;

            var trimmed = new List<ScoredChunk>(topK);
            for (int i = 0; i < topK; i++)
                trimmed.Add(lexicalResults[i]);
            return trimmed;
        }

        var scores = new Dictionary<string, (TextChunk Chunk, float Score)>(
            vectorResults.Count + lexicalResults.Count,
            StringComparer.Ordinal);

        for (int rank = 0; rank < vectorResults.Count; rank++)
        {
            var item = vectorResults[rank];
            float rrf = 1.0f / (kConstant + rank + 1);
            scores[item.Chunk.ChunkId] = (item.Chunk, rrf);
        }

        for (int rank = 0; rank < lexicalResults.Count; rank++)
        {
            var item = lexicalResults[rank];
            float rrf = 1.0f / (kConstant + rank + 1);
            if (scores.TryGetValue(item.Chunk.ChunkId, out var existing))
            {
                scores[item.Chunk.ChunkId] = (existing.Chunk, existing.Score + rrf);
            }
            else
            {
                scores[item.Chunk.ChunkId] = (item.Chunk, rrf);
            }
        }

        var merged = new List<ScoredChunk>(scores.Count);
        foreach (var kvp in scores)
        {
            merged.Add(new ScoredChunk(kvp.Value.Chunk, kvp.Value.Score));
        }

        merged.Sort(static (a, b) => b.SimilarityScore.CompareTo(a.SimilarityScore));

        if (merged.Count > topK)
        {
            merged.RemoveRange(topK, merged.Count - topK);
        }

        return merged;
    }
}

