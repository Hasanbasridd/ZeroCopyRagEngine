using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZeroCopyRagEngine.Application.DTOs;
using ZeroCopyRagEngine.Application.Security;
using ZeroCopyRagEngine.Application.Services;
using ZeroCopyRagEngine.Application.Telemetry;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Presentation.Controllers;

public sealed record DocumentIngestRequest(
    string FileId,
    string FileName,
    string Content,
    string? Source = null,
    IReadOnlyList<string>? Tags = null);

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("chat-limit")]
public class ChatController : ControllerBase
{
    private readonly ChatRetrievalService _chatRetrievalService;
    private readonly ITextChunker _textChunker;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorDatabase _vectorDatabase;
    private readonly IBm25Index? _bm25Index;
    private readonly RagSecurityOptions _securityOptions;

    public ChatController(
        ChatRetrievalService chatRetrievalService,
        ITextChunker textChunker,
        IEmbeddingService embeddingService,
        IVectorDatabase vectorDatabase,
        RagSecurityOptions securityOptions,
        IBm25Index? bm25Index = null)
    {
        _chatRetrievalService = chatRetrievalService;
        _textChunker = textChunker;
        _embeddingService = embeddingService;
        _vectorDatabase = vectorDatabase;
        _securityOptions = securityOptions;
        _bm25Index = bm25Index;
    }

    [HttpPost("ask-stream")]
    public async Task AskStream([FromBody] ChatQueryRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("Query cannot be empty.", cancellationToken);
            return;
        }

        if (request.Query.Length > _securityOptions.MaxQueryLength)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync($"Query exceeds maximum length of {_securityOptions.MaxQueryLength} characters.", cancellationToken);
            return;
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var stream = _chatRetrievalService.AskQuestionStreamAsync(request.Query, cancellationToken, request.Filter);

        await foreach (var token in stream.WithCancellation(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var message = $"data: {token.Replace("\n", "\\n")}\n\n";
            var bytes = Encoding.UTF8.GetBytes(message);
            await Response.Body.WriteAsync(bytes, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }

    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromBody] DocumentIngestRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Document content cannot be empty.");

        long byteSize = Encoding.UTF8.GetByteCount(request.Content);
        if (byteSize > _securityOptions.MaxUploadSizeBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, $"Document exceeds MaxUploadSizeBytes ({_securityOptions.MaxUploadSizeBytes}).");

        long startTimestamp = Stopwatch.GetTimestamp();
        using var activity = RagTelemetry.ActivitySource.StartActivity("ChatController.Ingest");

        var chunks = new List<TextChunk>();
        int idx = 0;

        foreach (var slice in _textChunker.ChunkText(request.Content.AsMemory(), maxTokens: 120, overlapTokens: 20))
        {
            if (idx >= _securityOptions.MaxChunkCount)
                return BadRequest($"Document produced more than MaxChunkCount ({_securityOptions.MaxChunkCount}) chunks.");

            float[] emb = await _embeddingService.GenerateEmbeddingAsync(slice, cancellationToken);
            var meta = new DocumentMetadata(
                FileId: request.FileId,
                FileName: request.FileName,
                PageNumber: 1,
                ChunkIndex: idx,
                Source: request.Source,
                CreatedAt: DateTimeOffset.UtcNow,
                Tags: request.Tags);

            chunks.Add(new TextChunk($"{request.FileId}_{idx}", slice, emb, meta));
            idx++;
        }

        await _vectorDatabase.UpsertChunksAsync(chunks, cancellationToken);
        _bm25Index?.IndexChunks(chunks);

        double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        RagTelemetry.IngestionDurationMs.Record(elapsedMs);

        return Ok(new { ingestedChunks = chunks.Count, durationMs = elapsedMs });
    }
}
