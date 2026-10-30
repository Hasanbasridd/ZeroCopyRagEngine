using System;
using System.Collections.Generic;
using ZeroCopyRagEngine.Domain.Entities;

namespace ZeroCopyRagEngine.Domain.Ports;

/// <summary>
/// Lexical inverted index port using Okapi BM25 scoring.
/// </summary>
public interface IBm25Index
{
    void IndexChunks(IReadOnlyList<TextChunk> chunks);
    IReadOnlyList<ScoredChunk> Search(ReadOnlySpan<char> query, int topK, MetadataFilter? filter = null);
}

