using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.Reranking;

/// <summary>
/// Fast cross-encoder-style lexical + bigram + semantic score fusion reranker.
/// Evaluates query-chunk term coverage, exact phrase overlap, and first-stage similarity score.
/// </summary>
public sealed class LexicalCrossEncoderReranker : IReranker
{
    private readonly float _firstStageWeight;
    private readonly float _overlapWeight;

    public LexicalCrossEncoderReranker(float firstStageWeight = 0.4f, float overlapWeight = 0.6f)
    {
        _firstStageWeight = firstStageWeight;
        _overlapWeight = overlapWeight;
    }

    public Task<IReadOnlyList<ScoredChunk>> RerankAsync(
        string query,
        IReadOnlyList<ScoredChunk> candidates,
        int topK,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0 || topK <= 0)
            return Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>());

        string[] queryTokens = ExtractTokens(query);
        var reranked = new List<ScoredChunk>(candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cand = candidates[i];
            ReadOnlySpan<char> contentSpan = cand.Chunk.Content.Span;

            float termCoverage = ComputeTermCoverage(queryTokens, contentSpan);
            float exactBoost = contentSpan.IndexOf(query.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase) >= 0 ? 0.25f : 0f;

            float combinedScore = (_firstStageWeight * cand.SimilarityScore) + (_overlapWeight * (termCoverage + exactBoost));
            reranked.Add(new ScoredChunk(cand.Chunk, combinedScore));
        }

        reranked.Sort(static (a, b) => b.SimilarityScore.CompareTo(a.SimilarityScore));
        if (reranked.Count > topK)
        {
            reranked.RemoveRange(topK, reranked.Count - topK);
        }

        return Task.FromResult<IReadOnlyList<ScoredChunk>>(reranked);
    }

    private static float ComputeTermCoverage(string[] queryTokens, ReadOnlySpan<char> content)
    {
        if (queryTokens.Length == 0 || content.IsEmpty)
            return 0f;

        int matched = 0;
        int bigramHits = 0;
        for (int i = 0; i < queryTokens.Length; i++)
        {
            if (content.IndexOf(queryTokens[i].AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matched++;
            }
            if (i + 1 < queryTokens.Length)
            {
                string bigram = queryTokens[i] + " " + queryTokens[i + 1];
                if (content.IndexOf(bigram.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bigramHits++;
                }
            }
        }

        float unigramScore = (float)matched / queryTokens.Length;
        float bigramScore = queryTokens.Length > 1 ? (float)bigramHits / (queryTokens.Length - 1) : 0f;
        return (0.7f * unigramScore) + (0.3f * bigramScore);
    }

    private static string[] ExtractTokens(string text)
    {
        var list = new List<string>();
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                int len = i - start;
                if (len >= 2)
                {
                    list.Add(text.Substring(start, len));
                }
                start = -1;
            }
        }
        return list.ToArray();
    }
}

