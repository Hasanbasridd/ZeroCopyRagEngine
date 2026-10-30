using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Microsoft.ML.Tokenizers;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.Chunkers;

/// <summary>
/// Token-aware zero-copy chunker backed by Microsoft.ML.Tokenizers.
/// Computes token boundaries and returns ReadOnlyMemory&lt;char&gt; slices of the original buffer without copying substring characters.
/// </summary>
public sealed class TokenAwareZeroCopyChunker : ITextChunker
{
    private static Tokenizer? s_defaultTokenizer;
    private readonly Tokenizer _tokenizer;

    public TokenAwareZeroCopyChunker(Tokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? GetDefaultTokenizer();
    }

    private static Tokenizer GetDefaultTokenizer()
    {
        return s_defaultTokenizer ??= CreateOfflineWordPieceTokenizer();
    }

    [SuppressMessage("Performance", "HAA0501", Justification = "One-time static vocabulary initialization for the offline Microsoft.ML.Tokenizers WordPiece instance.")]
    [SuppressMessage("Performance", "HAA0502", Justification = "One-time static vocabulary initialization for the offline Microsoft.ML.Tokenizers WordPiece instance.")]
    private static Tokenizer CreateOfflineWordPieceTokenizer()
    {
        const string vocabContent =
            "[UNK]\n[CLS]\n[SEP]\n[PAD]\n[MASK]\n" +
            ".\n,\n!\n?\n:\n;\n-\n(\n)\n[\n]\n{\n}\n/\n\\\n\"\n'\n#\n*\n`\n" +
            "the\nof\nand\nto\nin\nis\nfor\non\nthat\nwith\nas\nby\nthis\nare\nfrom\nbe\nat\nor\nan\nit\n" +
            "zero\ncopy\nrag\nengine\nsimd\nvector\nsearch\nchunk\nchunking\nmemory\nspan\narray\npool\n" +
            "embedding\ntoken\nstream\nsse\nhnsw\nbm25\nhybrid\nrerank\nquantization\nlatency\nrecall\n" +
            "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\nm\nn\no\np\nq\nr\ns\nt\nu\nv\nw\nx\ny\nz\n" +
            "0\n1\n2\n3\n4\n5\n6\n7\n8\n9\n" +
            "##s\n##ed\n##ing\n##er\n##ly\n##tion\n##ment\n##al\n##able\n##ive\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(vocabContent));
        return WordPieceTokenizer.Create(stream);
    }

    [SuppressMessage("Performance", "HAA0502", Justification = "Microsoft.ML.Tokenizers EncodeToTokens returns an IReadOnlyList<EncodedToken>; chunk slices themselves remain zero-copy ReadOnlyMemory<char> views over the original text.")]
    public IEnumerable<ReadOnlyMemory<char>> ChunkText(ReadOnlyMemory<char> text, int maxTokens, int overlapTokens)
    {
        if (text.IsEmpty)
            yield break;

        int safeMaxTokens = Math.Max(1, maxTokens);
        int safeOverlap = Math.Clamp(overlapTokens, 0, safeMaxTokens - 1);
        int step = Math.Max(1, safeMaxTokens - safeOverlap);

        IReadOnlyList<EncodedToken> tokens = _tokenizer.EncodeToTokens(text.Span, out _);
        int tokenCount = tokens.Count;

        if (tokenCount == 0)
        {
            yield return text;
            yield break;
        }

        if (tokenCount <= safeMaxTokens)
        {
            yield return text;
            yield break;
        }

        int estimatedChunks = (tokenCount / step) + 2;
        Range[] ranges = ArrayPool<Range>.Shared.Rent(estimatedChunks);
        int rangeCount = 0;

        try
        {
            int startTokenIdx = 0;
            while (startTokenIdx < tokenCount)
            {
                int endTokenIdx = Math.Min(tokenCount - 1, startTokenIdx + safeMaxTokens - 1);

                if (endTokenIdx < tokenCount - 1 && safeMaxTokens >= 8)
                {
                    int minBreakIdx = startTokenIdx + (int)(safeMaxTokens * 0.75);
                    for (int t = endTokenIdx; t >= minBreakIdx; t--)
                    {
                        Range tokenRange = tokens[t].Offset;
                        int len = tokenRange.End.Value - tokenRange.Start.Value;
                        if (len == 1)
                        {
                            char c = text.Span[tokenRange.Start.Value];
                            if (c == '.' || c == '!' || c == '?')
                            {
                                endTokenIdx = t;
                                break;
                            }
                        }
                    }
                }

                int charStart = tokens[startTokenIdx].Offset.Start.Value;
                int charEnd = tokens[endTokenIdx].Offset.End.Value;
                if (charEnd > charStart)
                {
                    if (rangeCount >= ranges.Length)
                    {
                        var newRanges = ArrayPool<Range>.Shared.Rent(ranges.Length * 2);
                        Array.Copy(ranges, newRanges, ranges.Length);
                        ArrayPool<Range>.Shared.Return(ranges);
                        ranges = newRanges;
                    }

                    ranges[rangeCount++] = new Range(charStart, charEnd);
                }

                if (endTokenIdx >= tokenCount - 1)
                    break;

                int consumedTokens = (endTokenIdx - startTokenIdx) + 1;
                startTokenIdx += Math.Max(1, consumedTokens - safeOverlap);
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
}
