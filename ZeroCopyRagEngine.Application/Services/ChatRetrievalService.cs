using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using ZeroCopyRagEngine.Application.Security;
using ZeroCopyRagEngine.Application.Telemetry;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Application.Services;

public sealed class ChatRetrievalService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorDatabase _vectorDatabase;
    private readonly ILLMService _llmService;
    private readonly IBm25Index? _bm25Index;
    private readonly IReranker? _reranker;
    private readonly RagSecurityOptions _securityOptions;

    public ChatRetrievalService(
        IEmbeddingService embeddingService,
        IVectorDatabase vectorDatabase,
        ILLMService llmService,
        IBm25Index? bm25Index = null,
        IReranker? reranker = null,
        RagSecurityOptions? securityOptions = null)
    {
        _embeddingService = embeddingService;
        _vectorDatabase = vectorDatabase;
        _llmService = llmService;
        _bm25Index = bm25Index;
        _reranker = reranker;
        _securityOptions = securityOptions ?? new RagSecurityOptions();
    }

    public async IAsyncEnumerable<string> AskQuestionStreamAsync(
        string userQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        MetadataFilter? filter = null)
    {
        if (string.IsNullOrWhiteSpace(userQuery))
            throw new ArgumentException("Query cannot be empty.", nameof(userQuery));

        if (userQuery.Length > _securityOptions.MaxQueryLength)
            throw new ArgumentOutOfRangeException(
                nameof(userQuery),
                $"Query length ({userQuery.Length}) exceeds maximum allowed limit ({_securityOptions.MaxQueryLength}).");

        using var activity = RagTelemetry.ActivitySource.StartActivity("ChatRetrievalService.AskQuestionStream");
        activity?.SetTag("rag.query.length", userQuery.Length);

        long startTimestamp = Stopwatch.GetTimestamp();

        float[] queryVector = await _embeddingService.GenerateEmbeddingAsync(userQuery.AsMemory(), cancellationToken);
        var vectorResults = await _vectorDatabase.SearchSimilarAsync(queryVector, topK: 5, filter, cancellationToken);

        IReadOnlyList<ScoredChunk> topChunks = vectorResults;
        if (_bm25Index is not null)
        {
            var lexicalResults = _bm25Index.Search(userQuery.AsSpan(), topK: 5, filter);
            topChunks = ReciprocalRankFusion.Fuse(vectorResults, lexicalResults, topK: 5);
        }

        if (_reranker is not null && topChunks.Count > 0)
        {
            topChunks = await _reranker.RerankAsync(userQuery, topChunks, topK: 5, cancellationToken);
        }

        double retrievalMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        RagTelemetry.RetrievalLatencyMs.Record(retrievalMs);
        activity?.SetTag("rag.retrieval.latency_ms", retrievalMs);
        activity?.SetTag("rag.retrieval.chunks_count", topChunks.Count);

        string systemPrompt = PromptSanitizer.BuildDefensiveSystemPrompt(topChunks);

        await foreach (var token in _llmService.GenerateStreamAsync(systemPrompt, userQuery, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RagTelemetry.StreamedTokensCounter.Add(1);
            yield return token;
        }
    }
}
