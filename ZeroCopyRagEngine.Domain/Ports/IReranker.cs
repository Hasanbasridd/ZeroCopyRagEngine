using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;

namespace ZeroCopyRagEngine.Domain.Ports;

/// <summary>
/// Reranks candidate chunks produced by first-stage retrieval (Vector / Hybrid RRF).
/// </summary>
public interface IReranker
{
    Task<IReadOnlyList<ScoredChunk>> RerankAsync(
        string query,
        IReadOnlyList<ScoredChunk> candidates,
        int topK,
        CancellationToken cancellationToken = default);
}

