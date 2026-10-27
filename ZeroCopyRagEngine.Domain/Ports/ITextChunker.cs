using System;
using System.Collections.Generic;

namespace ModernAiAssistant.Domain.Ports;

public interface ITextChunker
{
    /// <summary>
    /// Splits text using Zero-Copy primitives (Span/Memory) without string allocations.
    /// </summary>
    IEnumerable<ReadOnlyMemory<char>> ChunkText(ReadOnlyMemory<char> text, int maxTokens, int overlapTokens);
}
