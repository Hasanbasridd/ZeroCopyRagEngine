using System;
using System.Buffers;
using System.Collections.Generic;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.Chunkers;

/// <summary>
/// Structure-aware zero-copy chunker that respects Markdown heading boundaries (#, ##, ###),
/// paragraph breaks (\n\n), and sentence boundaries while operating exclusively on ReadOnlySpan&lt;char&gt; and ArrayPool&lt;Range&gt;.
/// </summary>
public sealed class MarkdownHierarchyZeroCopyChunker : ITextChunker
{
    public IEnumerable<ReadOnlyMemory<char>> ChunkText(ReadOnlyMemory<char> text, int maxTokens, int overlapTokens)
    {
        if (text.IsEmpty)
            yield break;

        int maxChars = Math.Max(8, maxTokens * 4);
        int overlapChars = Math.Clamp(overlapTokens * 4, 0, maxChars - 1);

        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        int estimatedChunks = (text.Length / Math.Max(1, maxChars - overlapChars)) + 4;
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
                    int minCut = (int)(currentChunkSize * 0.4);

                    // 1. Check for a Markdown heading (\n#) inside the window (after minCut)
                    int headingBreak = FindMarkdownHeadingBreak(window, minCut);
                    if (headingBreak > 0)
                    {
                        currentChunkSize = headingBreak;
                    }
                    else
                    {
                        // 2. Check for paragraph break (\n\n or \r\n\r\n)
                        int paragraphBreak = FindParagraphBreak(window, (int)(currentChunkSize * 0.6));
                        if (paragraphBreak > 0)
                        {
                            currentChunkSize = paragraphBreak;
                        }
                        else
                        {
                            // 3. Fallback to sentence boundary ('.', '!', '?') or word boundary (' ')
                            int lastPeriod = window.LastIndexOf('.');
                            int lastSpace = window.LastIndexOf(' ');

                            if (lastPeriod > currentChunkSize * 0.7)
                                currentChunkSize = lastPeriod + 1;
                            else if (lastSpace > currentChunkSize * 0.7)
                                currentChunkSize = lastSpace + 1;
                        }
                    }
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

                // If we broke cleanly at a Markdown heading, start the next chunk right at the heading
                bool brokeAtHeading = text.Span[currentIndex + currentChunkSize] == '#';
                if (brokeAtHeading)
                {
                    currentIndex += currentChunkSize;
                }
                else
                {
                    currentIndex += Math.Max(1, currentChunkSize - overlapChars);
                }
            }

            for (int i = 0; i < rangeCount; i++)
            {
                Range r = ranges[i];
                yield return text.Slice(r.Start.Value, r.End.Value - r.Start.Value);
            }
        }
        finally
        {
            ArrayPool<Range>.Shared.Return(ranges);
        }
    }

    private static int FindMarkdownHeadingBreak(ReadOnlySpan<char> window, int minIndex)
    {
        for (int i = window.Length - 2; i >= minIndex; i--)
        {
            if (window[i] == '\n' && window[i + 1] == '#')
            {
                return i + 1;
            }
        }
        return -1;
    }

    private static int FindParagraphBreak(ReadOnlySpan<char> window, int minIndex)
    {
        for (int i = window.Length - 2; i >= minIndex; i--)
        {
            if (window[i] == '\n' && window[i + 1] == '\n')
            {
                return i + 2;
            }
            if (i >= minIndex + 2 && window[i - 2] == '\r' && window[i - 1] == '\n' && window[i] == '\r' && window[i + 1] == '\n')
            {
                return i + 2;
            }
        }
        return -1;
    }
}

