using System;
using System.Buffers;
using System.Collections.Generic;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.Chunkers;

public sealed class ZeroCopySemanticOverlapChunker : ITextChunker
{
    public IEnumerable<ReadOnlyMemory<char>> ChunkText(ReadOnlyMemory<char> text, int maxTokens, int overlapTokens)
    {
        int maxChars = Math.Max(4, maxTokens * 4);
        int overlapChars = Math.Clamp(overlapTokens * 4, 0, maxChars - 1);

        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

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

                    if (lastPeriod > currentChunkSize * 0.7)
                        currentChunkSize = lastPeriod + 1;
                    else if (lastSpace > currentChunkSize * 0.7)
                        currentChunkSize = lastSpace + 1;
                }

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

    /// <summary>
    /// Computes chunk ranges directly into a caller-supplied span with strictly 0-byte heap allocation.
    /// </summary>
    public int ChunkTextSpan(ReadOnlySpan<char> text, int maxTokens, int overlapTokens, Span<Range> destination)
    {
        int maxChars = Math.Max(4, maxTokens * 4);
        int overlapChars = Math.Clamp(overlapTokens * 4, 0, maxChars - 1);

        if (text.Length == 0 || destination.Length == 0)
            return 0;

        if (text.Length <= maxChars)
        {
            destination[0] = new Range(0, text.Length);
            return 1;
        }

        int rangeCount = 0;
        int currentIndex = 0;
        while (currentIndex < text.Length && rangeCount < destination.Length)
        {
            int remaining = text.Length - currentIndex;
            int currentChunkSize = Math.Min(maxChars, remaining);

            if (currentIndex + currentChunkSize < text.Length)
            {
                ReadOnlySpan<char> window = text.Slice(currentIndex, currentChunkSize);
                int lastPeriod = window.LastIndexOf('.');
                int lastSpace = window.LastIndexOf(' ');

                if (lastPeriod > currentChunkSize * 0.7)
                    currentChunkSize = lastPeriod + 1;
                else if (lastSpace > currentChunkSize * 0.7)
                    currentChunkSize = lastSpace + 1;
            }

            destination[rangeCount++] = new Range(currentIndex, currentIndex + currentChunkSize);

            if (currentIndex + currentChunkSize >= text.Length)
                break;

            currentIndex += Math.Max(1, currentChunkSize - overlapChars);
        }

        return rangeCount;
    }
}
