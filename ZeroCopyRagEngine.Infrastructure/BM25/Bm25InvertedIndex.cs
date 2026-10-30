using System;
using System.Collections.Generic;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.BM25;

/// <summary>
/// In-memory Okapi BM25 inverted index for lexical keyword retrieval.
/// </summary>
public sealed class Bm25InvertedIndex : IBm25Index
{
    private readonly record struct Posting(int DocIndex, int TermFrequency);

    private readonly float _k1;
    private readonly float _b;
    private readonly object _syncLock = new();

    private readonly List<TextChunk> _documents = new();
    private readonly List<int> _docLengths = new();
    private readonly Dictionary<string, List<Posting>> _invertedIndex = new(StringComparer.OrdinalIgnoreCase);
    private double _averageDocLength;

    public Bm25InvertedIndex(float k1 = 1.5f, float b = 0.75f)
    {
        _k1 = k1;
        _b = b;
    }

    public int DocumentCount => _documents.Count;

    public void IndexChunks(IReadOnlyList<TextChunk> chunks)
    {
        if (chunks.Count == 0)
            return;

        lock (_syncLock)
        {
            var termCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                int docIdx = _documents.Count;
                _documents.Add(chunk);

                termCounts.Clear();
                int tokenCount = CountAndExtractTerms(chunk.Content.Span, termCounts);
                _docLengths.Add(tokenCount);

                foreach (var kvp in termCounts)
                {
                    if (!_invertedIndex.TryGetValue(kvp.Key, out var postings))
                    {
                        postings = new List<Posting>();
                        _invertedIndex[kvp.Key] = postings;
                    }
                    postings.Add(new Posting(docIdx, kvp.Value));
                }
            }

            long totalTokens = 0;
            for (int i = 0; i < _docLengths.Count; i++)
                totalTokens += _docLengths[i];

            _averageDocLength = _docLengths.Count > 0 ? (double)totalTokens / _docLengths.Count : 1.0;
        }
    }

    public IReadOnlyList<ScoredChunk> Search(ReadOnlySpan<char> query, int topK, MetadataFilter? filter = null)
    {
        if (topK <= 0 || _documents.Count == 0 || query.IsEmpty)
            return Array.Empty<ScoredChunk>();

        var queryTerms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        CountAndExtractTerms(query, queryTerms);
        if (queryTerms.Count == 0)
            return Array.Empty<ScoredChunk>();

        lock (_syncLock)
        {
            int docCount = _documents.Count;
            double avgDl = Math.Max(1.0, _averageDocLength);
            float[] scores = new float[docCount];

            foreach (var termEntry in queryTerms)
            {
                if (!_invertedIndex.TryGetValue(termEntry.Key, out var postings))
                    continue;

                int df = postings.Count;
                float idf = (float)Math.Log(1.0 + ((docCount - df + 0.5) / (df + 0.5)));

                for (int p = 0; p < postings.Count; p++)
                {
                    Posting posting = postings[p];
                    int docIdx = posting.DocIndex;
                    int tf = posting.TermFrequency;
                    int dl = _docLengths[docIdx];

                    float norm = _k1 * (1.0f - _b + _b * (float)(dl / avgDl));
                    float termScore = idf * ((tf * (_k1 + 1.0f)) / (tf + norm));
                    scores[docIdx] += termScore;
                }
            }

            var results = new List<ScoredChunk>();
            for (int i = 0; i < docCount; i++)
            {
                if (scores[i] <= 0f)
                    continue;

                var chunk = _documents[i];
                if (filter is not null && !filter.Matches(chunk.Metadata))
                    continue;

                results.Add(new ScoredChunk(chunk, scores[i]));
            }

            results.Sort(static (a, b) => b.SimilarityScore.CompareTo(a.SimilarityScore));
            if (results.Count > topK)
            {
                results.RemoveRange(topK, results.Count - topK);
            }

            return results;
        }
    }

    private static int CountAndExtractTerms(ReadOnlySpan<char> text, Dictionary<string, int> termFrequencies)
    {
        int totalTerms = 0;
        int start = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isWordChar = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWordChar)
            {
                if (start < 0)
                    start = i;
            }
            else if (start >= 0)
            {
                int len = i - start;
                if (len >= 2)
                {
                    string term = text.Slice(start, len).ToString().ToLowerInvariant();
                    termFrequencies.TryGetValue(term, out int current);
                    termFrequencies[term] = current + 1;
                    totalTerms++;
                }
                start = -1;
            }
        }

        return totalTerms;
    }
}

