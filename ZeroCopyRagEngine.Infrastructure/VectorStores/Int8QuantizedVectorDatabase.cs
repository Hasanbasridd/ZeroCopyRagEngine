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

/// <summary>
/// Scalar-quantized (int8 / sbyte) vector database reducing vector storage memory by 4x compared to float32.
/// Each vector v is quantized to sbyte[-127..127] with a per-vector scale factor and precomputed L2 norm.
/// </summary>
public sealed class Int8QuantizedVectorDatabase : IVectorDatabase
{
    private readonly ConcurrentDictionary<string, TextChunk> _storage = new();
    private readonly object _syncLock = new();

    private sbyte[] _quantizedVectors = Array.Empty<sbyte>();
    private float[] _scales = Array.Empty<float>();
    private string[] _vectorIds = Array.Empty<string>();
    private int _vectorDimension;
    private int _vectorCount;

    public int Count => _vectorCount;
    public int Dimension => _vectorDimension;
    public long QuantizedBytesAllocated => _quantizedVectors.LongLength + (_scales.LongLength * sizeof(float));

    [SuppressMessage("Performance", "HAA0501", Justification = "Ingestion path: allocates contiguous sbyte[] and float[] scale buffers per upsert batch.")]
    [SuppressMessage("Performance", "HAA0502", Justification = "Ingestion path: stores chunk metadata in ConcurrentDictionary.")]
    public Task UpsertChunksAsync(IReadOnlyList<TextChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
            return Task.CompletedTask;

        lock (_syncLock)
        {
            if (_vectorDimension == 0)
                _vectorDimension = chunks[0].Embedding.Length;

            int dim = _vectorDimension;
            int newTotal = _vectorCount + chunks.Count;

            sbyte[] newQuantized = new sbyte[newTotal * dim];
            float[] newScales = new float[newTotal];
            string[] newIds = new string[newTotal];

            if (_vectorCount > 0)
            {
                Array.Copy(_quantizedVectors, newQuantized, _vectorCount * dim);
                Array.Copy(_scales, newScales, _vectorCount);
                Array.Copy(_vectorIds, newIds, _vectorCount);
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                _storage[chunk.ChunkId] = chunk;

                int destIdx = _vectorCount + i;
                newIds[destIdx] = chunk.ChunkId;

                ReadOnlySpan<float> src = chunk.Embedding.AsSpan(0, dim);
                Span<sbyte> dst = newQuantized.AsSpan(destIdx * dim, dim);
                newScales[destIdx] = QuantizeVector(src, dst);
            }

            _quantizedVectors = newQuantized;
            _scales = newScales;
            _vectorIds = newIds;
            _vectorCount = newTotal;
        }

        return Task.CompletedTask;
    }

    public static float QuantizeVector(ReadOnlySpan<float> source, Span<sbyte> destination)
    {
        float maxAbs = 0f;
        for (int i = 0; i < source.Length; i++)
        {
            float abs = MathF.Abs(source[i]);
            if (abs > maxAbs)
                maxAbs = abs;
        }

        if (maxAbs < 1e-12f)
        {
            destination.Clear();
            return 1f;
        }

        float scale = maxAbs / 127f;
        float invScale = 127f / maxAbs;

        for (int i = 0; i < source.Length; i++)
        {
            int q = (int)MathF.Round(source[i] * invScale);
            destination[i] = (sbyte)Math.Clamp(q, -127, 127);
        }

        return scale;
    }

    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        CancellationToken cancellationToken = default)
        => SearchSimilarAsync(queryVector, topK, filter: null, cancellationToken);

    [SuppressMessage("Performance", "HAA0502", Justification = "Materializes the final List<ScoredChunk> return value; dequantization and SIMD scoring use ArrayPool buffers with 0 heap allocations in the O(N) loop.")]
    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter,
        CancellationToken cancellationToken = default)
    {
        if (_vectorCount == 0 || topK <= 0)
            return Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>());

        ReadOnlySpan<float> querySpan = queryVector.Span;
        sbyte[] quantizedRef = _quantizedVectors;
        float[] scalesRef = _scales;
        string[] idsRef = _vectorIds;
        int count = _vectorCount;
        int dim = _vectorDimension;
        int effectiveK = Math.Min(topK, count);

        float[] topScores = ArrayPool<float>.Shared.Rent(effectiveK);
        int[] topIndices = ArrayPool<int>.Shared.Rent(effectiveK);
        float[] dequantizedBuffer = ArrayPool<float>.Shared.Rent(dim);

        try
        {
            Span<float> scoresSpan = topScores.AsSpan(0, effectiveK);
            Span<int> indicesSpan = topIndices.AsSpan(0, effectiveK);
            Span<float> dequantSpan = dequantizedBuffer.AsSpan(0, dim);
            int selectedCount = 0;

            for (int i = 0; i < count; i++)
            {
                if (filter is not null)
                {
                    string candidateId = idsRef[i];
                    if (!_storage.TryGetValue(candidateId, out var candidateChunk) || !filter.Matches(candidateChunk.Metadata))
                        continue;
                }

                float scale = scalesRef[i];
                int baseOffset = i * dim;
                for (int j = 0; j < dim; j++)
                {
                    dequantSpan[j] = quantizedRef[baseOffset + j] * scale;
                }

                float score = TensorPrimitives.CosineSimilarity(querySpan, dequantSpan);
                TopKSelector.InsertCandidate(score, i, scoresSpan, indicesSpan, ref selectedCount, effectiveK);
            }

            var results = new List<ScoredChunk>(selectedCount);
            for (int i = 0; i < selectedCount; i++)
            {
                int idx = indicesSpan[i];
                string id = idsRef[idx];
                if (_storage.TryGetValue(id, out var chunk))
                {
                    results.Add(new ScoredChunk(chunk, scoresSpan[i]));
                }
            }

            return Task.FromResult<IReadOnlyList<ScoredChunk>>(results);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(dequantizedBuffer);
            ArrayPool<float>.Shared.Return(topScores);
            ArrayPool<int>.Shared.Return(topIndices);
        }
    }

    public static double CalculateRecallAtK(IReadOnlyList<ScoredChunk> exactResults, IReadOnlyList<ScoredChunk> approximateResults, int k)
    {
        int evalK = Math.Min(k, exactResults.Count);
        if (evalK == 0)
            return 1.0;

        int hits = 0;
        int approxLimit = Math.Min(k, approximateResults.Count);
        for (int i = 0; i < evalK; i++)
        {
            string exactId = exactResults[i].Chunk.ChunkId;
            for (int j = 0; j < approxLimit; j++)
            {
                if (string.Equals(exactId, approximateResults[j].Chunk.ChunkId, StringComparison.Ordinal))
                {
                    hits++;
                    break;
                }
            }
        }

        return (double)hits / evalK;
    }
}

