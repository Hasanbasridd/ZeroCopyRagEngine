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
    
    // Flattened array for ultra-fast L1/L2 Cache friendly SIMD processing
    private float[] _vectorStore = Array.Empty<float>();
    private string[] _vectorIds = Array.Empty<string>();
    private int _vectorDimension;
    private int _vectorCount;
    private readonly object _syncLock = new();

    [SuppressMessage("Performance", "HAA0501", Justification = "Ingestion path, not the search hot path: the flattened store is grown once per upsert batch.")]
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

    [SuppressMessage("Performance", "HAA0502", Justification = "Result materialization: one List<ScoredChunk> and topK ScoredChunk instances per query, required by the IVectorDatabase contract. The O(N) scoring loop itself does not allocate.")]
    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(ReadOnlyMemory<float> queryVector, int topK, CancellationToken cancellationToken = default)
    {
        if (_vectorCount == 0)
            return Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>());

        ReadOnlySpan<float> querySpan = queryVector.Span;
        float[] vectorStoreRef = _vectorStore; // Local ref to avoid lock contention
        string[] vectorIdsRef = _vectorIds;
        int count = _vectorCount;
        int dim = _vectorDimension;

        // 0-Byte GC Allocation during search via ArrayPool
        float[] scores = ArrayPool<float>.Shared.Rent(count);
        int[] indices = ArrayPool<int>.Shared.Rent(count);

        try
        {
            // Hardware-accelerated batch Cosine Similarity (AVX-512 / NEON if hardware supports)
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<float> targetVector = new ReadOnlySpan<float>(vectorStoreRef, i * dim, dim);
                scores[i] = TensorPrimitives.CosineSimilarity(querySpan, targetVector);
                indices[i] = i;
            }

            // Simple sort based on scores descending. For ultra-large sets, QuickSelect is better,
            // but Array.Sort is sufficiently fast for typical local vector DBs in memory.
            Array.Sort(scores, indices, 0, count, Comparer<float>.Create((a, b) => b.CompareTo(a)));

            var results = new List<ScoredChunk>(topK);
            for (int i = 0; i < Math.Min(topK, count); i++)
            {
                int originalIndex = indices[i];
                string id = vectorIdsRef[originalIndex];
                if (_storage.TryGetValue(id, out var chunk))
                {
                    results.Add(new ScoredChunk(chunk, scores[i]));
                }
            }

            return Task.FromResult<IReadOnlyList<ScoredChunk>>(results);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scores);
            ArrayPool<int>.Shared.Return(indices);
        }
    }
}
