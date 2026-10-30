using System;
using System.Collections.Generic;

namespace ZeroCopyRagEngine.Domain.Ports;

public interface ITextChunker
{
    /// <summary>
    /// Splits text using Zero-Copy primitives (Span/Memory) without substring copies.
    /// </summary>
    IEnumerable<ReadOnlyMemory<char>> ChunkText(ReadOnlyMemory<char> text, int maxTokens, int overlapTokens);
}
