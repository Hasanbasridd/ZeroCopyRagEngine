using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using ZeroCopyRagEngine.Application.Services;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Infrastructure.BM25;
using ZeroCopyRagEngine.Infrastructure.Reranking;
using ZeroCopyRagEngine.Infrastructure.VectorStores;

namespace ZeroCopyRagEngine.Tests;

public class VectorStoreAndHybridTests
{
    [Fact]
    public async Task AllFourVectorStores_Should_Return_Expected_TopK_Order()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"zcr_all4_{Guid.NewGuid():N}.vec");
        try
        {
            var flat = new SimdLocalVectorDatabase();
            var int8 = new Int8QuantizedVectorDatabase();
            var hnsw = new HnswVectorDatabase(m: 16, efConstruction: 64, efSearch: 64);
            using var mmf = new MemoryMappedVectorDatabase(tempPath);

            var meta = new DocumentMetadata("f1", "doc.md", 1, 0, "spec", DateTimeOffset.UtcNow, new[] { "core" });
            var chunks = new[]
            {
                new TextChunk("best", "Best match".AsMemory(), new float[] { 1.0f, 0.0f, 0.0f, 0.0f }, meta),
                new TextChunk("second", "Second match".AsMemory(), new float[] { 0.8f, 0.6f, 0.0f, 0.0f }, meta),
                new TextChunk("orthogonal", "Orthogonal".AsMemory(), new float[] { 0.0f, 1.0f, 0.0f, 0.0f }, meta),
                new TextChunk("opposite", "Opposite".AsMemory(), new float[] { -1.0f, 0.0f, 0.0f, 0.0f }, meta)
            };

            await flat.UpsertChunksAsync(chunks);
            await int8.UpsertChunksAsync(chunks);
            await hnsw.UpsertChunksAsync(chunks);
            await mmf.UpsertChunksAsync(chunks);

            var query = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };

            var flatRes = await flat.SearchSimilarAsync(query, topK: 2);
            var int8Res = await int8.SearchSimilarAsync(query, topK: 2);
            var hnswRes = await hnsw.SearchSimilarAsync(query, topK: 2);
            var mmfRes = await mmf.SearchSimilarAsync(query, topK: 2);

            flatRes.Select(x => x.Chunk.ChunkId).Should().Equal("best", "second");
            int8Res.Select(x => x.Chunk.ChunkId).Should().Equal("best", "second");
            hnswRes.Select(x => x.Chunk.ChunkId).Should().Equal("best", "second");
            mmfRes.Select(x => x.Chunk.ChunkId).Should().Equal("best", "second");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(tempPath + ".meta")) File.Delete(tempPath + ".meta");
        }
    }

    [Fact]
    public async Task Hnsw_And_Int8_Should_Maintain_High_Recall_Against_Flat_Exact_Baseline()
    {
        var flat = new SimdLocalVectorDatabase();
        var int8 = new Int8QuantizedVectorDatabase();
        var hnsw = new HnswVectorDatabase(m: 16, efConstruction: 80, efSearch: 80);

        var rng = new Random(2026);
        int count = 400;
        int dim = 48;
        var meta = new DocumentMetadata("f1", "bench.txt", 1, 0);
        var chunks = new List<TextChunk>(count);

        for (int i = 0; i < count; i++)
        {
            var vec = new float[dim];
            for (int d = 0; d < dim; d++) vec[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
            chunks.Add(new TextChunk($"v_{i}", $"content {i}".AsMemory(), vec, meta));
        }

        await flat.UpsertChunksAsync(chunks);
        await int8.UpsertChunksAsync(chunks);
        await hnsw.UpsertChunksAsync(chunks);

        double totalInt8Recall = 0;
        double totalHnswRecall = 0;
        int queries = 10;

        for (int q = 0; q < queries; q++)
        {
            var queryVec = chunks[q * 30].Embedding;
            var exactTop10 = await flat.SearchSimilarAsync(queryVec, 10);
            var int8Top10 = await int8.SearchSimilarAsync(queryVec, 10);
            var hnswTop10 = await hnsw.SearchSimilarAsync(queryVec, 10);

            totalInt8Recall += Int8QuantizedVectorDatabase.CalculateRecallAtK(exactTop10, int8Top10, 10);
            totalHnswRecall += Int8QuantizedVectorDatabase.CalculateRecallAtK(exactTop10, hnswTop10, 10);
        }

        (totalInt8Recall / queries).Should().BeGreaterThanOrEqualTo(0.95);
        (totalHnswRecall / queries).Should().BeGreaterThanOrEqualTo(0.85);
    }

    [Fact]
    public async Task Bm25_And_ReciprocalRankFusion_Should_Produce_Deterministic_Known_Ranking()
    {
        var meta = new DocumentMetadata("f1", "doc.md", 1, 0);
        var c1 = new TextChunk("doc_A", "HAA0601 boxing allocation error in Roslyn analyzer".AsMemory(), new float[] { 1f, 0f }, meta);
        var c2 = new TextChunk("doc_B", "HAA0601 HAA0601 value type boxing diagnostic rule".AsMemory(), new float[] { 0.8f, 0.2f }, meta);
        var c3 = new TextChunk("doc_C", "Unrelated Kubernetes deployment manifest".AsMemory(), new float[] { 0f, 1f }, meta);

        var bm25 = new Bm25InvertedIndex();
        bm25.IndexChunks(new[] { c1, c2, c3 });

        var bm25Results = bm25.Search("HAA0601 boxing".AsSpan(), topK: 3);
        bm25Results.Should().HaveCount(2);
        // doc_B has higher term frequency for HAA0601 and shorter length
        bm25Results[0].Chunk.ChunkId.Should().Be("doc_B");
        bm25Results[1].Chunk.ChunkId.Should().Be("doc_A");

        // Known RRF example:
        // Vector ranks: [doc_A (rank 1), doc_C (rank 2), doc_B (rank 3)]
        // BM25 ranks:   [doc_B (rank 1), doc_A (rank 2)]
        // With kConstant=60:
        // score(doc_A) = 1/(60+1) + 1/(60+2) = 1/61 + 1/62 = 0.0325224
        // score(doc_B) = 1/(60+3) + 1/(60+1) = 1/63 + 1/61 = 0.0322665
        // score(doc_C) = 1/(60+2)            = 1/62        = 0.0161290
        var vectorList = new List<ScoredChunk>
        {
            new(c1, 0.99f),
            new(c3, 0.70f),
            new(c2, 0.65f)
        };
        var lexicalList = new List<ScoredChunk>
        {
            new(c2, 4.2f),
            new(c1, 3.1f)
        };

        var fused = ReciprocalRankFusion.Fuse(vectorList, lexicalList, topK: 3, kConstant: 60);
        fused.Select(f => f.Chunk.ChunkId).Should().Equal("doc_A", "doc_B", "doc_C");

        var reranker = new LexicalCrossEncoderReranker();
        var reranked = await reranker.RerankAsync("HAA0601 value type boxing", fused, topK: 2);
        reranked.Should().HaveCount(2);
    }

    [Fact]
    public async Task MetadataFilter_Should_Filter_By_FileId_Source_DateRange_And_RequiredTags()
    {
        var now = DateTimeOffset.UtcNow;
        var metaOld = new DocumentMetadata("file_old", "old.md", 1, 0, "archive", now.AddDays(-10), new[] { "legacy", "v1" });
        var metaNew = new DocumentMetadata("file_new", "new.md", 1, 0, "production", now, new[] { "simd", "v2" });

        var chunkOld = new TextChunk("c_old", "SIMD vector search engine".AsMemory(), new float[] { 1f, 0f }, metaOld);
        var chunkNew = new TextChunk("c_new", "SIMD vector search engine".AsMemory(), new float[] { 1f, 0f }, metaNew);

        var flat = new SimdLocalVectorDatabase();
        var int8 = new Int8QuantizedVectorDatabase();
        var hnsw = new HnswVectorDatabase();
        var bm25 = new Bm25InvertedIndex();

        await flat.UpsertChunksAsync(new[] { chunkOld, chunkNew });
        await int8.UpsertChunksAsync(new[] { chunkOld, chunkNew });
        await hnsw.UpsertChunksAsync(new[] { chunkOld, chunkNew });
        bm25.IndexChunks(new[] { chunkOld, chunkNew });

        var filterProd = new MetadataFilter(
            FileId: "file_new",
            Source: "production",
            MinCreatedAt: now.AddDays(-1),
            MaxCreatedAt: now.AddDays(1),
            RequiredTags: new[] { "simd", "v2" });

        (await flat.SearchSimilarAsync(new float[] { 1f, 0f }, 5, filterProd)).Should().ContainSingle(x => x.Chunk.ChunkId == "c_new");
        (await int8.SearchSimilarAsync(new float[] { 1f, 0f }, 5, filterProd)).Should().ContainSingle(x => x.Chunk.ChunkId == "c_new");
        (await hnsw.SearchSimilarAsync(new float[] { 1f, 0f }, 5, filterProd)).Should().ContainSingle(x => x.Chunk.ChunkId == "c_new");
        bm25.Search("SIMD vector".AsSpan(), 5, filterProd).Should().ContainSingle(x => x.Chunk.ChunkId == "c_new");
    }
}
