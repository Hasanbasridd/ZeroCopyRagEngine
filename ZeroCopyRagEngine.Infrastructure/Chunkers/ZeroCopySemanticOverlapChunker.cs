using System;
using System.Buffers;
using System.Collections.Generic;
using ModernAiAssistant.Domain.Ports;

namespace ModernAiAssistant.Infrastructure.Chunkers;

public sealed class ZeroCopySemanticOverlapChunker : ITextChunker
{
    public IEnumerable<ReadOnlyMemory<char>> ChunkText(ReadOnlyMemory<char> text, int maxTokens, int overlapTokens)
    {
        // Approximation: 1 token ~= 4 chars
        int maxChars = maxTokens * 4;
        int overlapChars = overlapTokens * 4;

        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        // Use ArrayPool<Range> to perform all index calculations with 0-byte GC allocation
        // before yielding the results.
        int estimatedChunks = (text.Length / Math.Max(1, maxChars - overlapChars)) + 2;
        Range[] ranges = ArrayPool<Range>.Shared.Rent(estimatedChunks);
        int rangeCount = 0;

        try
        {
            int currentIndex = 0;
            while (currentIndex < text.Length)
            {
                int remaining = text.Length - currentIndex;
                int currentChunkSize = Math.Min(maxChars, remaining);

                if (currentIndex + currentChunkSize < text.Length)
                {
                    ReadOnlySpan<char> window = text.Span.Slice(currentIndex, currentChunkSize);
                    int lastPeriod = window.LastIndexOf('.');
                    int lastSpace = window.LastIndexOf(' ');

                    if (lastPeriod > currentChunkSize * 0.7) // Prefer sentence boundary
                        currentChunkSize = lastPeriod + 1;
                    else if (lastSpace > currentChunkSize * 0.7) // Fallback to word boundary
                        currentChunkSize = lastSpace + 1;
                }

                // Resize array if necessary (should be extremely rare with correct estimation)
                if (rangeCount >= ranges.Length)
                {
                    var newRanges = ArrayPool<Range>.Shared.Rent(ranges.Length * 2);
                    Array.Copy(ranges, newRanges, ranges.Length);
                    ArrayPool<Range>.Shared.Return(ranges);
                    ranges = newRanges;
                }

                ranges[rangeCount++] = new Range(currentIndex, currentIndex + currentChunkSize);

                if (currentIndex + currentChunkSize >= text.Length)
                    break;

                currentIndex += Math.Max(1, currentChunkSize - overlapChars);
            }

            // Yield the pre-calculated memory slices. 
            // Yielding allocates a state machine once per method call, but the loop itself is 0 allocation.
            for (int i = 0; i < rangeCount; i++)
            {
                var r = ranges[i];
                yield return text.Slice(r.Start.Value, r.End.Value - r.Start.Value);
            }
        }
        finally
        {
            ArrayPool<Range>.Shared.Return(ranges);
        }
    }
}
