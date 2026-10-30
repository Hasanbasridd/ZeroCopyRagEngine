using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;
using ZeroCopyRagEngine.Application.Services;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Infrastructure.BM25;
using ZeroCopyRagEngine.Infrastructure.Chunkers;
using ZeroCopyRagEngine.Infrastructure.DocumentLoaders;
using ZeroCopyRagEngine.Infrastructure.Embedding;
using ZeroCopyRagEngine.Infrastructure.Evaluation;
using ZeroCopyRagEngine.Infrastructure.LLM;
using ZeroCopyRagEngine.Infrastructure.Providers;
using ZeroCopyRagEngine.Infrastructure.Reranking;
using ZeroCopyRagEngine.Infrastructure.VectorStores;

namespace ZeroCopyRagEngine.Tests;

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
    public void ZeroCopyChunker_ChunkTextSpan_Should_Allocate_Zero_Bytes()
    {
        var text = string.Join(" ", Enumerable.Repeat("Zero allocation span chunking test sentence.", 50));
        var chunker = new ZeroCopySemanticOverlapChunker();
        var ranges = new Range[256];

        chunker.ChunkTextSpan(text.AsSpan(), 20, 4, ranges);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = chunker.ChunkTextSpan(text.AsSpan(), 20, 4, ranges);
        long after = GC.GetAllocatedBytesForCurrentThread();

        count.Should().BeGreaterThan(1);
        (after - before).Should().Be(0);
    }

    [Fact]
    public void TokenAware_And_MarkdownHierarchy_Chunkers_Should_Preserve_Structure()
    {
        string markdown = "# Section One\nFirst paragraph with detailed technical content about SIMD vector search.\n\n# Section Two\nSecond paragraph discussing memory-mapped files and zero-copy slices.";
        var tokenChunker = new TokenAwareZeroCopyChunker();
        var mdChunker = new MarkdownHierarchyZeroCopyChunker();

        var tokenChunks = tokenChunker.ChunkText(markdown.AsMemory(), maxTokens: 12, overlapTokens: 2).ToList();
        var mdChunks = mdChunker.ChunkText(markdown.AsMemory(), maxTokens: 18, overlapTokens: 2).ToList();

        tokenChunks.Should().HaveCountGreaterThan(1);
        mdChunks.Should().HaveCountGreaterThan(1);
        mdChunks.Any(c => c.Span.ToString().StartsWith("# Section Two")).Should().BeTrue();
    }

    [Fact]
    public async Task DocumentLoaders_Should_Load_PlainText_Markdown_And_Pdf()
    {
        var txtLoader = new PlainTextDocumentLoader();
        var mdLoader = new MarkdownDocumentLoader();
        var pdfLoader = new PdfDocumentLoader();

        txtLoader.CanHandle(".txt").Should().BeTrue();
        mdLoader.CanHandle(".md").Should().BeTrue();
        pdfLoader.CanHandle(".pdf").Should().BeTrue();

        using var txtStream = new MemoryStream(Encoding.UTF8.GetBytes("Hello plain text world."));
        var txtDoc = await txtLoader.LoadAsync(txtStream, "sample.txt");
        txtDoc.Pages.Should().ContainSingle();

        using var mdStream = new MemoryStream(Encoding.UTF8.GetBytes("# Intro\nHello\n# Architecture\nZero copy"));
        var mdDoc = await mdLoader.LoadAsync(mdStream, "readme.md");
        mdDoc.Pages.Should().HaveCount(2);
        mdDoc.Pages[1].SectionHeader.Should().Be("Architecture");

        string minimalPdf = "%PDF-1.4\n1 0 obj\nstream\nBT (Zero Copy PDF Page 1) Tj ET\nendstream\n";
        using var pdfStream = new MemoryStream(Encoding.Latin1.GetBytes(minimalPdf));
        var pdfDoc = await pdfLoader.LoadAsync(pdfStream, "spec.pdf");
        pdfDoc.Pages.Should().ContainSingle();
        pdfDoc.Pages[0].RawText.Span.ToString().Should().Contain("Zero Copy PDF Page 1");
    }

    [Fact]
    public async Task SimdLocalVectorDatabase_Should_Calculate_CosineSimilarity_Correctly_And_ZeroAlloc_On_Span()
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

        int[] topIndices = new int[2];
        float[] topScores = new float[2];
        db.SearchSimilarSpan(query, topIndices, topScores);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = db.SearchSimilarSpan(query, topIndices, topScores);
        long after = GC.GetAllocatedBytesForCurrentThread();

        count.Should().Be(2);
        topIndices[0].Should().Be(0);
        (after - before).Should().Be(0);
    }

    [Fact]
    public async Task MemoryMappedVectorDatabase_Should_Persist_And_Read_ZeroCopy_Across_Instances()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"zcr_test_{Guid.NewGuid():N}.vec");
        try
        {
            var meta1 = new DocumentMetadata("f1", "a.md", 1, 0, "docs", DateTimeOffset.UtcNow, new[] { "simd" });
            var meta2 = new DocumentMetadata("f2", "b.md", 1, 0, "adr", DateTimeOffset.UtcNow, new[] { "mmap" });

            using (var writerDb = new MemoryMappedVectorDatabase(tempPath))
            {
                await writerDb.UpsertChunksAsync(new[]
                {
                    new TextChunk("c1", "SIMD cosine similarity".AsMemory(), new float[] { 1f, 0f, 0f, 0f }, meta1),
                    new TextChunk("c2", "Memory mapped file persistence".AsMemory(), new float[] { 0f, 1f, 0f, 0f }, meta2)
                });
            }

            using (var readerDb = new MemoryMappedVectorDatabase(tempPath))
            {
                readerDb.Count.Should().Be(2);
                var results = await readerDb.SearchSimilarAsync(new float[] { 0f, 1f, 0f, 0f }, topK: 1);
                results.Should().ContainSingle();
                results[0].Chunk.ChunkId.Should().Be("c2");
                results[0].Chunk.Content.Span.ToString().Should().Be("Memory mapped file persistence");

                var filtered = await readerDb.SearchSimilarAsync(
                    new float[] { 0f, 1f, 0f, 0f },
                    topK: 2,
                    new MetadataFilter(RequiredTags: new[] { "simd" }));
                filtered.Should().ContainSingle();
                filtered[0].Chunk.ChunkId.Should().Be("c1");
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(tempPath + ".meta")) File.Delete(tempPath + ".meta");
        }
    }

    [Fact]
    public async Task Int8Quantized_And_Hnsw_VectorDatabases_Should_Achieve_High_Recall()
    {
        var flatDb = new SimdLocalVectorDatabase();
        var int8Db = new Int8QuantizedVectorDatabase();
        var hnswDb = new HnswVectorDatabase(m: 16, efConstruction: 64, efSearch: 64);

        var rng = new Random(123);
        int count = 500;
        int dim = 64;
        var chunks = new List<TextChunk>(count);
        var meta = new DocumentMetadata("f1", "f1.txt", 1, 1);

        for (int i = 0; i < count; i++)
        {
            var vec = new float[dim];
            for (int j = 0; j < dim; j++) vec[j] = (float)(rng.NextDouble() * 2.0 - 1.0);
            chunks.Add(new TextChunk($"id_{i}", $"chunk {i}".AsMemory(), vec, meta));
        }

        await flatDb.UpsertChunksAsync(chunks);
        await int8Db.UpsertChunksAsync(chunks);
        await hnswDb.UpsertChunksAsync(chunks);

        var query = chunks[42].Embedding;
        var exactTop10 = await flatDb.SearchSimilarAsync(query, 10);
        var int8Top10 = await int8Db.SearchSimilarAsync(query, 10);
        var hnswTop10 = await hnswDb.SearchSimilarAsync(query, 10);

        double int8Recall = Int8QuantizedVectorDatabase.CalculateRecallAtK(exactTop10, int8Top10, 10);
        double hnswRecall = Int8QuantizedVectorDatabase.CalculateRecallAtK(exactTop10, hnswTop10, 10);

        int8Recall.Should().BeGreaterThanOrEqualTo(0.90);
        hnswRecall.Should().BeGreaterThanOrEqualTo(0.80);
        hnswTop10[0].Chunk.ChunkId.Should().Be("id_42");
    }

    [Fact]
    public async Task HybridSearch_BM25_RRF_And_Reranker_Should_Combine_Lexical_And_Vector_Signals()
    {
        var embedding = new DummyEmbeddingService();
        var vectorDb = new SimdLocalVectorDatabase();
        var bm25 = new Bm25InvertedIndex();
        var reranker = new LexicalCrossEncoderReranker();

        var c1Text = "TensorPrimitives CosineSimilarity accelerates AVX-512 vector search.";
        var c2Text = "Okapi BM25 inverted index scores keyword term frequency with k1=1.5 and b=0.75.";
        var c3Text = "Server-Sent Events stream tokens asynchronously over HTTP.";

        var chunks = new[]
        {
            new TextChunk("c1", c1Text.AsMemory(), await embedding.GenerateEmbeddingAsync(c1Text.AsMemory()), new DocumentMetadata("f1", "1.md", 1, 0, "arch", DateTimeOffset.UtcNow, new[] { "simd" })),
            new TextChunk("c2", c2Text.AsMemory(), await embedding.GenerateEmbeddingAsync(c2Text.AsMemory()), new DocumentMetadata("f2", "2.md", 1, 0, "arch", DateTimeOffset.UtcNow, new[] { "bm25" })),
            new TextChunk("c3", c3Text.AsMemory(), await embedding.GenerateEmbeddingAsync(c3Text.AsMemory()), new DocumentMetadata("f3", "3.md", 1, 0, "api", DateTimeOffset.UtcNow, new[] { "sse" }))
        };

        await vectorDb.UpsertChunksAsync(chunks);
        bm25.IndexChunks(chunks);

        var hybrid = new HybridRetrievalService(embedding, vectorDb, bm25, reranker);
        var results = await hybrid.RetrieveAsync("Okapi BM25 k1=1.5 keyword term frequency", topK: 2);

        results.Should().NotBeEmpty();
        results[0].Chunk.ChunkId.Should().Be("c2");

        var filtered = await hybrid.RetrieveAsync("search", topK: 3, filter: new MetadataFilter(Source: "api"));
        filtered.Should().ContainSingle(r => r.Chunk.ChunkId == "c3");
    }

    [Fact]
    public async Task MicrosoftExtensionsAi_Adapters_Should_Bridge_Domain_Ports_Cleanly()
    {
        IChatClient meaiChat = new DomainToMeaiChatClientAdapter(new DummyLLMService());
        var domainLlm = new MeaiChatClientLlmAdapter(meaiChat);

        var tokens = new List<string>();
        await foreach (var token in domainLlm.GenerateStreamAsync("system", "user"))
        {
            tokens.Add(token);
        }
        tokens.Should().NotBeEmpty();

        IEmbeddingGenerator<string, Embedding<float>> meaiEmb = new DomainToMeaiEmbeddingGeneratorAdapter(new DummyEmbeddingService());
        var domainEmb = new MeaiEmbeddingServiceAdapter(meaiEmb);
        float[] vec = await domainEmb.GenerateEmbeddingAsync("zero copy rag".AsMemory());
        vec.Should().HaveCount(384);
    }

    [Fact]
    public async Task GoldenDataset_Evaluation_Should_Meet_All_Thresholds()
    {
        string datasetPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "datasets", "golden-dataset.json"));
        File.Exists(datasetPath).Should().BeTrue();

        var dataset = RagEvaluator.LoadGoldenDataset(datasetPath);
        dataset.Questions.Count.Should().BeInRange(30, 50);

        var hybridReport = await RagEvaluator.EvaluateConfigurationAsync(
            "MarkdownHierarchy + Hybrid (SIMD + BM25 + RRF + Reranker)",
            dataset,
            new MarkdownHierarchyZeroCopyChunker(),
            new SimdLocalVectorDatabase(),
            new DummyEmbeddingService(),
            useHybridBm25: true,
            useReranker: true,
            k: 5);

        hybridReport.RecallAtK.Should().BeGreaterThanOrEqualTo(0.90);
        hybridReport.MrrAtK.Should().BeGreaterThanOrEqualTo(0.85);
        hybridReport.NdcgAtK.Should().BeGreaterThanOrEqualTo(0.85);
        hybridReport.Faithfulness.Should().BeGreaterThan(0.80);
    }
}
