using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.Evaluation;

public sealed class GoldenDataset
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("documents")]
    public List<GoldenDocument> Documents { get; set; } = new();

    [JsonPropertyName("questions")]
    public List<GoldenQuestion> Questions { get; set; } = new();
}

public sealed class GoldenDocument
{
    [JsonPropertyName("chunkId")]
    public string ChunkId { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public sealed class GoldenQuestion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("expectedChunkIds")]
    public List<string> ExpectedChunkIds { get; set; } = new();

    [JsonPropertyName("relevanceGrades")]
    public Dictionary<string, int> RelevanceGrades { get; set; } = new();

    [JsonPropertyName("referenceAnswer")]
    public string ReferenceAnswer { get; set; } = string.Empty;
}

public sealed record EvaluationReport(
    string ConfigurationName,
    int QuestionCount,
    int K,
    double RecallAtK,
    double MrrAtK,
    double NdcgAtK,
    double Faithfulness,
    double AnswerRelevance,
    double AverageLatencyMs);

public interface ILlmAnswerJudge
{
    Task<(double Faithfulness, double AnswerRelevance)> ScoreAnswerAsync(
        string question,
        string retrievedContext,
        string generatedAnswer,
        string referenceAnswer,
        CancellationToken cancellationToken = default);
}

public sealed class DeterministicGroundingJudge : ILlmAnswerJudge
{
    public Task<(double Faithfulness, double AnswerRelevance)> ScoreAnswerAsync(
        string question,
        string retrievedContext,
        string generatedAnswer,
        string referenceAnswer,
        CancellationToken cancellationToken = default)
    {
        double faithfulness = ComputeTokenOverlap(generatedAnswer, retrievedContext);
        double refSimilarity = ComputeTokenOverlap(generatedAnswer, referenceAnswer);
        double questionCoverage = ComputeTokenOverlap(generatedAnswer, question);
        double relevance = Math.Clamp((0.65 * refSimilarity) + (0.35 * questionCoverage), 0.0, 1.0);

        return Task.FromResult((faithfulness, relevance));
    }

    private static double ComputeTokenOverlap(string candidate, string referenceText)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(referenceText))
            return 0.0;

        var candidateTerms = ExtractContentTerms(candidate);
        if (candidateTerms.Count == 0)
            return 0.0;

        var refSet = new HashSet<string>(ExtractContentTerms(referenceText), StringComparer.OrdinalIgnoreCase);
        int hits = 0;
        for (int i = 0; i < candidateTerms.Count; i++)
        {
            if (refSet.Contains(candidateTerms[i]))
                hits++;
        }

        return (double)hits / candidateTerms.Count;
    }

    private static List<string> ExtractContentTerms(string text)
    {
        var list = new List<string>();
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                int len = i - start;
                if (len >= 3)
                {
                    list.Add(text.Substring(start, len));
                }
                start = -1;
            }
        }
        return list;
    }
}

public static class RagEvaluator
{
    public static GoldenDataset LoadGoldenDataset(string jsonFilePath)
    {
        string json = File.ReadAllText(jsonFilePath);
        return JsonSerializer.Deserialize<GoldenDataset>(json)
               ?? throw new InvalidOperationException($"Failed to deserialize golden dataset from '{jsonFilePath}'.");
    }

    public static double CalculateRecallAtK(IReadOnlyList<ScoredChunk> retrieved, IReadOnlyList<string> expectedChunkIds, int k)
    {
        if (expectedChunkIds.Count == 0)
            return 1.0;

        int limit = Math.Min(k, retrieved.Count);
        int hits = 0;

        for (int e = 0; e < expectedChunkIds.Count; e++)
        {
            string expected = expectedChunkIds[e];
            for (int r = 0; r < limit; r++)
            {
                if (MatchChunkId(retrieved[r].Chunk.ChunkId, expected))
                {
                    hits++;
                    break;
                }
            }
        }

        return (double)hits / expectedChunkIds.Count;
    }

    public static double CalculateMrrAtK(IReadOnlyList<ScoredChunk> retrieved, IReadOnlyList<string> expectedChunkIds, int k)
    {
        int limit = Math.Min(k, retrieved.Count);
        for (int r = 0; r < limit; r++)
        {
            string actualId = retrieved[r].Chunk.ChunkId;
            for (int e = 0; e < expectedChunkIds.Count; e++)
            {
                if (MatchChunkId(actualId, expectedChunkIds[e]))
                {
                    return 1.0 / (r + 1);
                }
            }
        }

        return 0.0;
    }

    public static double CalculateNdcgAtK(IReadOnlyList<ScoredChunk> retrieved, IReadOnlyDictionary<string, int> relevanceGrades, int k)
    {
        if (relevanceGrades.Count == 0)
            return 1.0;

        int limit = Math.Min(k, retrieved.Count);
        double dcg = 0.0;

        for (int r = 0; r < limit; r++)
        {
            string actualId = retrieved[r].Chunk.ChunkId;
            int rel = GetRelevanceGrade(actualId, relevanceGrades);
            if (rel > 0)
            {
                double gain = (1 << rel) - 1;
                double discount = Math.Log2(r + 2);
                dcg += gain / discount;
            }
        }

        var idealGrades = new List<int>(relevanceGrades.Values);
        idealGrades.Sort(static (a, b) => b.CompareTo(a));

        int idealLimit = Math.Min(k, idealGrades.Count);
        double idcg = 0.0;
        for (int i = 0; i < idealLimit; i++)
        {
            int rel = idealGrades[i];
            if (rel > 0)
            {
                double gain = (1 << rel) - 1;
                double discount = Math.Log2(i + 2);
                idcg += gain / discount;
            }
        }

        return idcg > 0.0 ? dcg / idcg : 0.0;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HAA0401", Justification = "Offline evaluation helper iterates over dictionary and chunker interfaces.")]
    private static int GetRelevanceGrade(string actualChunkId, IReadOnlyDictionary<string, int> relevanceGrades)
    {
        if (relevanceGrades.TryGetValue(actualChunkId, out int grade))
            return grade;

        foreach (var kvp in relevanceGrades)
        {
            if (MatchChunkId(actualChunkId, kvp.Key))
                return kvp.Value;
        }

        return 0;
    }

    private static bool MatchChunkId(string actualChunkId, string expectedDocId)
    {
        return string.Equals(actualChunkId, expectedDocId, StringComparison.OrdinalIgnoreCase)
               || actualChunkId.StartsWith(expectedDocId + "_", StringComparison.OrdinalIgnoreCase);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HAA0401", Justification = "Offline evaluation runner iterates over ITextChunker.")]
    public static async Task<EvaluationReport> EvaluateConfigurationAsync(
        string configurationName,
        GoldenDataset dataset,
        ITextChunker chunker,
        IVectorDatabase vectorDatabase,
        IEmbeddingService embeddingService,
        bool useHybridBm25 = false,
        bool useReranker = false,
        int k = 5,
        int maxTokensPerChunk = 80,
        int overlapTokens = 15,
        ILlmAnswerJudge? judge = null,
        CancellationToken cancellationToken = default)
    {
        judge ??= new DeterministicGroundingJudge();
        var chunksToIngest = new List<TextChunk>();

        for (int d = 0; d < dataset.Documents.Count; d++)
        {
            var doc = dataset.Documents[d];
            int partIndex = 0;
            foreach (var slice in chunker.ChunkText(doc.Content.AsMemory(), maxTokensPerChunk, overlapTokens))
            {
                string chunkId = partIndex == 0 ? doc.ChunkId : $"{doc.ChunkId}_p{partIndex.ToString()}";
                float[] emb = await embeddingService.GenerateEmbeddingAsync(slice, cancellationToken);
                var meta = new DocumentMetadata(
                    FileId: doc.ChunkId,
                    FileName: doc.FileName,
                    PageNumber: 1,
                    ChunkIndex: partIndex,
                    Source: doc.Source,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Tags: doc.Tags);

                chunksToIngest.Add(new TextChunk(chunkId, slice, emb, meta));
                partIndex++;
            }
        }

        await vectorDatabase.UpsertChunksAsync(chunksToIngest, cancellationToken);

        double totalRecall = 0;
        double totalMrr = 0;
        double totalNdcg = 0;
        double totalFaithfulness = 0;
        double totalRelevance = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int q = 0; q < dataset.Questions.Count; q++)
        {
            var question = dataset.Questions[q];
            float[] queryEmb = await embeddingService.GenerateEmbeddingAsync(question.Question.AsMemory(), cancellationToken);
            var retrieved = await vectorDatabase.SearchSimilarAsync(queryEmb, topK: k, cancellationToken: cancellationToken);

            totalRecall += CalculateRecallAtK(retrieved, question.ExpectedChunkIds, k);
            totalMrr += CalculateMrrAtK(retrieved, question.ExpectedChunkIds, k);
            totalNdcg += CalculateNdcgAtK(retrieved, question.RelevanceGrades, k);

            string topContext = retrieved.Count > 0 ? retrieved[0].Chunk.Content.Span.ToString() : string.Empty;
            string synthesizedAnswer = topContext;
            var (faith, rel) = await judge.ScoreAnswerAsync(
                question.Question,
                topContext,
                synthesizedAnswer,
                question.ReferenceAnswer,
                cancellationToken);

            totalFaithfulness += faith;
            totalRelevance += rel;
        }

        sw.Stop();
        int n = Math.Max(1, dataset.Questions.Count);

        return new EvaluationReport(
            ConfigurationName: configurationName,
            QuestionCount: dataset.Questions.Count,
            K: k,
            RecallAtK: totalRecall / n,
            MrrAtK: totalMrr / n,
            NdcgAtK: totalNdcg / n,
            Faithfulness: totalFaithfulness / n,
            AnswerRelevance: totalRelevance / n,
            AverageLatencyMs: sw.Elapsed.TotalMilliseconds / n);
    }
}
