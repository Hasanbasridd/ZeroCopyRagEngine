using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using ModernAiAssistant.Infrastructure.Chunkers;

namespace ModernAiAssistant.Benchmarks;

[MemoryDiagnoser]
public class ChunkerBenchmarks
{
    private readonly ZeroCopySemanticOverlapChunker _zeroCopyChunker = new();
    private ReadOnlyMemory<char> _text;
    private string _rawText = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _rawText = string.Join(" ", Enumerable.Repeat("This is a mock enterprise document used for benchmarking zero-byte allocation.", 1000));
        _text = _rawText.AsMemory();
    }

    [Benchmark(Baseline = true)]
    public int TraditionalStringSplit()
    {
        var chunks = _rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return chunks.Length;
    }

    [Benchmark]
    public int ZeroCopySpanChunking()
    {
        int count = 0;
        foreach (var chunk in _zeroCopyChunker.ChunkText(_text, 100, 20))
        {
            count++;
        }
        return count;
    }
}
