using System;
using System.Linq;
using FluentAssertions;
using Xunit;
using ZeroCopyRagEngine.Infrastructure.Chunkers;

namespace ZeroCopyRagEngine.Tests;

public class ChunkerTests
{
    [Fact]
    public void AllChunkers_Should_Handle_Empty_And_Short_Text_Gracefully()
    {
        var whitespaceChunker = new ZeroCopySemanticOverlapChunker();
        var tokenChunker = new TokenAwareZeroCopyChunker();
        var markdownChunker = new MarkdownHierarchyZeroCopyChunker();

        var emptyMem = ReadOnlyMemory<char>.Empty;

        whitespaceChunker.ChunkText(emptyMem, 10, 2).Should().ContainSingle().Which.Length.Should().Be(0);
        tokenChunker.ChunkText(emptyMem, 10, 2).Should().BeEmpty();
        markdownChunker.ChunkText(emptyMem, 10, 2).Should().BeEmpty();

        Span<Range> ranges = stackalloc Range[8];
        whitespaceChunker.ChunkTextSpan(ReadOnlySpan<char>.Empty, 10, 2, ranges).Should().Be(0);
    }

    [Fact]
    public void AllChunkers_Should_Split_Single_Long_Unbroken_Word_Without_Infinite_Loop()
    {
        string unbrokenWord = new string('X', 240);
        var whitespaceChunker = new ZeroCopySemanticOverlapChunker();
        var tokenChunker = new TokenAwareZeroCopyChunker();
        var markdownChunker = new MarkdownHierarchyZeroCopyChunker();

        var wsChunks = whitespaceChunker.ChunkText(unbrokenWord.AsMemory(), maxTokens: 10, overlapTokens: 2).ToList();
        var tkChunks = tokenChunker.ChunkText(unbrokenWord.AsMemory(), maxTokens: 10, overlapTokens: 2).ToList();
        var mdChunks = markdownChunker.ChunkText(unbrokenWord.AsMemory(), maxTokens: 10, overlapTokens: 2).ToList();

        wsChunks.Count.Should().BeGreaterThan(1);
        tkChunks.Should().NotBeEmpty();
        mdChunks.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void AllChunkers_Should_Preserve_Unicode_And_Turkish_Characters_Without_Corruption()
    {
        string turkishText =
            "Çekirdek sıfır-kopya (zero-copy) bellek mimarisi, ğüşıöç ve ĞÜŞİÖÇ karakterlerini " +
            "bozmadan yüksek başarımlı vektör araması yapar. İkinci cümle de Türkçe karakter içerir ve " +
            "bellek önbelleği (L1/L2) dostu çalışır.";

        var whitespaceChunker = new ZeroCopySemanticOverlapChunker();
        var tokenChunker = new TokenAwareZeroCopyChunker();
        var markdownChunker = new MarkdownHierarchyZeroCopyChunker();

        var wsChunks = whitespaceChunker.ChunkText(turkishText.AsMemory(), maxTokens: 12, overlapTokens: 3).ToList();
        var tkChunks = tokenChunker.ChunkText(turkishText.AsMemory(), maxTokens: 12, overlapTokens: 3).ToList();
        var mdChunks = markdownChunker.ChunkText(turkishText.AsMemory(), maxTokens: 12, overlapTokens: 3).ToList();

        string.Concat(wsChunks.Select(c => c.Span.ToString())).Should().Contain("ğüşıöç").And.Contain("ĞÜŞİÖÇ");
        string.Concat(tkChunks.Select(c => c.Span.ToString())).Should().Contain("ğüşıöç").And.Contain("ĞÜŞİÖÇ");
        string.Concat(mdChunks.Select(c => c.Span.ToString())).Should().Contain("ğüşıöç").And.Contain("ĞÜŞİÖÇ");
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(10, 9)]
    [InlineData(10, 25)] // Overlap larger than maxTokens should clamp safely
    [InlineData(1, 5)]
    public void Chunkers_Should_Clamp_Extreme_Overlap_Boundaries_Safely(int maxTokens, int overlapTokens)
    {
        string sample = string.Join(" ", Enumerable.Repeat("Boundary clamp verification sentence.", 15));
        var whitespaceChunker = new ZeroCopySemanticOverlapChunker();
        var tokenChunker = new TokenAwareZeroCopyChunker();
        var markdownChunker = new MarkdownHierarchyZeroCopyChunker();

        var wsChunks = whitespaceChunker.ChunkText(sample.AsMemory(), maxTokens, overlapTokens).ToList();
        var tkChunks = tokenChunker.ChunkText(sample.AsMemory(), maxTokens, overlapTokens).ToList();
        var mdChunks = markdownChunker.ChunkText(sample.AsMemory(), maxTokens, overlapTokens).ToList();

        wsChunks.Should().NotBeEmpty();
        tkChunks.Should().NotBeEmpty();
        mdChunks.Should().NotBeEmpty();
    }

    [Fact]
    public void MarkdownHierarchyZeroCopyChunker_Should_Keep_Heading_Hierarchy_Intact()
    {
        string markdown =
            "# 1. Architecture Overview\n" +
            "ZeroCopyRagEngine uses ReadOnlySpan<char> and MemoryMappedFile.\n\n" +
            "## 1.1 Vector Indexing\n" +
            "Supports Flat SIMD, Int8 scalar quantization, and HNSW multi-layer graphs.\n\n" +
            "### 1.1.1 Hybrid Retrieval\n" +
            "Combines Okapi BM25 with Reciprocal Rank Fusion (k=60).";

        var mdChunker = new MarkdownHierarchyZeroCopyChunker();
        var chunks = mdChunker.ChunkText(markdown.AsMemory(), maxTokens: 16, overlapTokens: 2).ToList();

        chunks.Count.Should().BeGreaterThanOrEqualTo(3);
        chunks[0].Span.ToString().Should().StartWith("# 1. Architecture Overview");
        chunks.Any(c => c.Span.ToString().Contains("## 1.1 Vector Indexing")).Should().BeTrue();
        chunks.Any(c => c.Span.ToString().Contains("### 1.1.1 Hybrid Retrieval")).Should().BeTrue();
    }
}
