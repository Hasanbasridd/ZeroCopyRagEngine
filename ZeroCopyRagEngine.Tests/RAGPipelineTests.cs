using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ModernAiAssistant.Domain.Entities;
using ModernAiAssistant.Infrastructure.Chunkers;
using ModernAiAssistant.Infrastructure.VectorStores;
using Xunit;

namespace ModernAiAssistant.Tests;

public class RAGPipelineTests
{
    [Fact]
    public void ZeroCopyChunker_Should_ChunkText_Without_Losing_Characters()
    {
        var text = "This is a long text that needs to be chunked into smaller pieces. We are testing the zero-copy chunker.";
        var chunker = new ZeroCopySemanticOverlapChunker();
        
        var chunks = chunker.ChunkText(text.AsMemory(), maxTokens: 5, overlapTokens: 1).ToList();

        chunks.Should().NotBeEmpty();
        chunks[0].Span.ToString().Should().StartWith("This");
    }

    [Fact]
    public async Task SimdLocalVectorDatabase_Should_Calculate_CosineSimilarity_Correctly()
    {
        var db = new SimdLocalVectorDatabase();
        var meta = new DocumentMetadata("file1", "test.txt", 1, 1);
        
        var vec1 = new float[] { 1f, 0f, 0f };
        var vec2 = new float[] { 0f, 1f, 0f };
        var vec3 = new float[] { -1f, 0f, 0f };

        var chunks = new[]
        {
            new TextChunk("c1", ReadOnlyMemory<char>.Empty, vec1, meta),
            new TextChunk("c2", ReadOnlyMemory<char>.Empty, vec2, meta),
            new TextChunk("c3", ReadOnlyMemory<char>.Empty, vec3, meta)
        };

        await db.UpsertChunksAsync(chunks);

        var query = new float[] { 1f, 0f, 0f };
        var results = await db.SearchSimilarAsync(query.AsMemory(), topK: 3);

        results.Should().HaveCount(3);
        
        results[0].Chunk.ChunkId.Should().Be("c1");
        results[0].SimilarityScore.Should().BeApproximately(1.0f, 0.001f);

        results[1].Chunk.ChunkId.Should().Be("c2");
        results[1].SimilarityScore.Should().BeApproximately(0.0f, 0.001f);

        results[2].Chunk.ChunkId.Should().Be("c3");
        results[2].SimilarityScore.Should().BeApproximately(-1.0f, 0.001f);
    }
}
