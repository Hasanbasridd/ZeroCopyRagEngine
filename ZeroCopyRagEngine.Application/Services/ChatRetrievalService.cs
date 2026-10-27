using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using ModernAiAssistant.Domain.Ports;

namespace ModernAiAssistant.Application.Services;

public sealed class ChatRetrievalService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorDatabase _vectorDatabase;
    private readonly ILLMService _llmService;

    public ChatRetrievalService(IEmbeddingService embeddingService, IVectorDatabase vectorDatabase, ILLMService llmService)
    {
        _embeddingService = embeddingService;
        _vectorDatabase = vectorDatabase;
        _llmService = llmService;
    }

    public async IAsyncEnumerable<string> AskQuestionStreamAsync(string userQuery, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        float[] queryVector = await _embeddingService.GenerateEmbeddingAsync(userQuery.AsMemory(), cancellationToken);
        var topChunks = await _vectorDatabase.SearchSimilarAsync(queryVector, topK: 5, cancellationToken);

        var sb = new StringBuilder(2048);
        sb.AppendLine("You are a helpful and precise corporate assistant.");
        sb.AppendLine("Answer the user's question explicitly using ONLY the provided context chunks below.");
        sb.AppendLine("\n--- CONTEXT CHUNKS ---");

        foreach (var scoredChunk in topChunks)
        {
            var meta = scoredChunk.Chunk.Metadata;
            sb.Append($"[File: {meta.FileName} | Page: {meta.PageNumber}] ");
            sb.Append(scoredChunk.Chunk.Content.Span);
            sb.AppendLine();
        }

        string systemPrompt = sb.ToString();

        await foreach (var token in _llmService.GenerateStreamAsync(systemPrompt, userQuery, cancellationToken))
        {
            yield return token;
        }
    }
}
