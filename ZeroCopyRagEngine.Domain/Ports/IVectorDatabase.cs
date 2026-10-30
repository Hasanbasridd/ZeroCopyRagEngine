using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;

namespace ZeroCopyRagEngine.Domain.Ports;

public interface IVectorDatabase
{
    Task UpsertChunksAsync(IReadOnlyList<TextChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leverages SIMD (System.Numerics.Tensors) for vector similarity retrieval.
    /// </summary>
    Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(ReadOnlyMemory<float> queryVector, int topK, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches similar chunks with optional metadata filtering.
    /// </summary>
    Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(ReadOnlyMemory<float> queryVector, int topK, MetadataFilter? filter, CancellationToken cancellationToken = default)
        => SearchSimilarAsync(queryVector, topK, cancellationToken);
}
