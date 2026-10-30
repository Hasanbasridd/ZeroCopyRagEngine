using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.VectorStores;

public sealed class SimdLocalVectorDatabase : IVectorDatabase
{
    private readonly ConcurrentDictionary<string, TextChunk> _storage = new();

    // Flattened array for L1/L2 Cache friendly SIMD processing
    private float[] _vectorStore = Array.Empty<float>();
    private string[] _vectorIds = Array.Empty<string>();
    private int _vectorDimension;
    private int _vectorCount;
    private readonly object _syncLock = new();

    public int Count => _vectorCount;
    public int Dimension => _vectorDimension;

    [SuppressMessage("Performance", "HAA0501", Justification = "Ingestion path, not the search hot path: the flattened store is grown once per upsert batch.")]
    [SuppressMessage("Performance", "HAA0502", Justification = "Lock contention and dictionary storage occur only during batch ingestion.")]
    public Task UpsertChunksAsync(IReadOnlyList<TextChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0) return Task.CompletedTask;

        lock (_syncLock)
        {
            if (_vectorDimension == 0)
                _vectorDimension = chunks[0].Embedding.Length;

            int newTotal = _vectorCount + chunks.Count;
            float[] newVectorStore = new float[newTotal * _vectorDimension];
            string[] newVectorIds = new string[newTotal];

            if (_vectorCount > 0)
            {
                Array.Copy(_vectorStore, newVectorStore, _vectorCount * _vectorDimension);
                Array.Copy(_vectorIds, newVectorIds, _vectorCount);
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                _storage[chunk.ChunkId] = chunk;

                int destIndex = _vectorCount + i;
                newVectorIds[destIndex] = chunk.ChunkId;
                Array.Copy(chunk.Embedding, 0, newVectorStore, destIndex * _vectorDimension, _vectorDimension);
            }

            _vectorStore = newVectorStore;
            _vectorIds = newVectorIds;
            _vectorCount = newTotal;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Strictly 0-byte GC allocation SIMD vector search writing top-K indices and scores directly into caller spans.
    /// </summary>
    public int SearchSimilarSpan(ReadOnlySpan<float> queryVector, Span<int> topIndices, Span<float> topScores)
    {
        int count = _vectorCount;
        int dim = _vectorDimension;
        int maxK = Math.Min(topIndices.Length, topScores.Length);
        if (count == 0 || dim == 0 || maxK <= 0)
            return 0;

        float[] vectorStoreRef = _vectorStore;
        int selectedCount = 0;

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<float> targetVector = new ReadOnlySpan<float>(vectorStoreRef, i * dim, dim);
            float score = TensorPrimitives.CosineSimilarity(queryVector, targetVector);
            TopKSelector.InsertCandidate(score, i, topScores, topIndices, ref selectedCount, maxK);
        }

        return selectedCount;
    }

    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        CancellationToken cancellationToken = default)
        => SearchSimilarAsync(queryVector, topK, filter: null, cancellationToken);

    [SuppressMessage("Performance", "HAA0502", Justification = "Result materialization: one List<ScoredChunk> and topK ScoredChunk instances per query, required by the IVectorDatabase contract. The O(N) scoring and Top-K selection loop itself does not allocate.")]
    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter,
        CancellationToken cancellationToken = default)
    {
        if (_vectorCount == 0 || topK <= 0)
            return Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>());

        ReadOnlySpan<float> querySpan = queryVector.Span;
        float[] vectorStoreRef = _vectorStore;
        string[] vectorIdsRef = _vectorIds;
        int count = _vectorCount;
        int dim = _vectorDimension;
        int effectiveK = Math.Min(topK, count);

        float[] topScores = ArrayPool<float>.Shared.Rent(effectiveK);
        int[] topIndices = ArrayPool<int>.Shared.Rent(effectiveK);

        try
        {
            Span<float> scoresSpan = topScores.AsSpan(0, effectiveK);
            Span<int> indicesSpan = topIndices.AsSpan(0, effectiveK);
            int selectedCount = 0;

            for (int i = 0; i < count; i++)
            {
                if (filter is not null)
                {
                    string candidateId = vectorIdsRef[i];
                    if (!_storage.TryGetValue(candidateId, out var candidateChunk) || !filter.Matches(candidateChunk.Metadata))
                        continue;
                }

                ReadOnlySpan<float> targetVector = new ReadOnlySpan<float>(vectorStoreRef, i * dim, dim);
                float score = TensorPrimitives.CosineSimilarity(querySpan, targetVector);
                TopKSelector.InsertCandidate(score, i, scoresSpan, indicesSpan, ref selectedCount, effectiveK);
            }

            var results = new List<ScoredChunk>(selectedCount);
            for (int i = 0; i < selectedCount; i++)
            {
                int originalIndex = indicesSpan[i];
                string id = vectorIdsRef[originalIndex];
                if (_storage.TryGetValue(id, out var chunk))
                {
                    results.Add(new ScoredChunk(chunk, scoresSpan[i]));
                }
            }

            return Task.FromResult<IReadOnlyList<ScoredChunk>>(results);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(topScores);
            ArrayPool<int>.Shared.Return(topIndices);
        }
    }
}
