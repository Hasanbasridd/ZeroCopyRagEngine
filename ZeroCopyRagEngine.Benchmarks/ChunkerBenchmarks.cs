using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using ZeroCopyRagEngine.Infrastructure.Chunkers;

namespace ZeroCopyRagEngine.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[RankColumn]
public class ChunkerBenchmarks
{
    private readonly ZeroCopySemanticOverlapChunker _zeroCopyChunker = new();
    private readonly TokenAwareZeroCopyChunker _tokenAwareChunker = new();
    private readonly MarkdownHierarchyZeroCopyChunker _markdownChunker = new();
    private ReadOnlyMemory<char> _text;
    private string _rawText = string.Empty;
    private Range[] _preallocatedRanges = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rawText = string.Join(
            " ",
            Enumerable.Repeat(
                "# Architectural Section\nThis is a realistic enterprise technical document used for benchmarking chunking throughput and GC heap allocation discipline.",
                1000));
        _text = _rawText.AsMemory();
        _preallocatedRanges = new Range[2048];
    }

    [Benchmark(Baseline = true, Description = "Baseline 1: string.Split + StringBuilder")]
    public int TraditionalStringSplit()
    {
        string[] words = _rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int maxChars = 400;
        var chunks = new List<string>();
        var sb = new StringBuilder(maxChars);

        for (int i = 0; i < words.Length; i++)
        {
            if (sb.Length + words[i].Length + 1 > maxChars && sb.Length > 0)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(words[i]);
        }
        if (sb.Length > 0)
            chunks.Add(sb.ToString());

        return chunks.Count;
    }

    [Benchmark(Description = "Baseline 2: Naive Span Boundary + Substring Allocation")]
    public int NaiveSpanSubstringChunker()
    {
        int maxChars = 400;
        int overlapChars = 80;
        ReadOnlySpan<char> span = _rawText.AsSpan();
        var chunks = new List<string>();

        int currentIndex = 0;
        while (currentIndex < span.Length)
        {
            int remaining = span.Length - currentIndex;
            int currentChunkSize = Math.Min(maxChars, remaining);

            if (currentIndex + currentChunkSize < span.Length)
            {
                ReadOnlySpan<char> window = span.Slice(currentIndex, currentChunkSize);
                int lastPeriod = window.LastIndexOf('.');
                int lastSpace = window.LastIndexOf(' ');

                if (lastPeriod > currentChunkSize * 0.7)
                    currentChunkSize = lastPeriod + 1;
                else if (lastSpace > currentChunkSize * 0.7)
                    currentChunkSize = lastSpace + 1;
            }

            // Allocates a new string per chunk (standard naive approach)
            chunks.Add(span.Slice(currentIndex, currentChunkSize).ToString());

            if (currentIndex + currentChunkSize >= span.Length)
                break;

            currentIndex += Math.Max(1, currentChunkSize - overlapChars);
        }

        return chunks.Count;
    }

    [Benchmark(Description = "Baseline 3: Microsoft.ML.Tokenizers TokenAwareZeroCopyChunker")]
    public int MicrosoftMlTokenizerChunker()
    {
        int count = 0;
        foreach (var chunk in _tokenAwareChunker.ChunkText(_text, 100, 20))
        {
            count += chunk.Length > 0 ? 1 : 0;
        }
        return count;
    }

    [Benchmark(Description = "ZeroCopySemanticOverlapChunker (IEnumerable<ReadOnlyMemory<char>>)")]
    public int ZeroCopySpanChunking()
    {
        int count = 0;
        foreach (var chunk in _zeroCopyChunker.ChunkText(_text, 100, 20))
        {
            count += chunk.Length > 0 ? 1 : 0;
        }
        return count;
    }

    [Benchmark(Description = "ZeroCopySemanticOverlapChunker.ChunkTextSpan (Strictly 0 B)")]
    public int ZeroCopySpanDestinationBuffer()
    {
        return _zeroCopyChunker.ChunkTextSpan(_rawText.AsSpan(), 100, 20, _preallocatedRanges);
    }

    [Benchmark(Description = "MarkdownHierarchyZeroCopyChunker (Structure-Aware)")]
    public int MarkdownHierarchyZeroCopyChunking()
    {
        int count = 0;
        foreach (var chunk in _markdownChunker.ChunkText(_text, 100, 20))
        {
            count += chunk.Length > 0 ? 1 : 0;
        }
        return count;
    }
}
