using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.Providers;

/// <summary>
/// Adapts the official Microsoft.Extensions.AI.IChatClient to the Domain ILLMService port.
/// Keeps Clean Architecture Domain layer free of external package dependencies.
/// </summary>
public sealed class MeaiChatClientLlmAdapter : ILLMService
{
    private readonly IChatClient _chatClient;

    public MeaiChatClientLlmAdapter(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>(2)
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, options: null, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }
}

/// <summary>
/// Adapts Domain ILLMService to the official Microsoft.Extensions.AI.IChatClient contract.
/// </summary>
public sealed class DomainToMeaiChatClientAdapter : IChatClient
{
    private readonly ILLMService _llmService;

    public DomainToMeaiChatClientAdapter(ILLMService llmService)
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HAA0401", Justification = "Official IChatClient contract accepts IEnumerable<ChatMessage>.")]
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            sb.Append(update.Text);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()))
        {
            ModelId = options?.ModelId ?? "domain-adapter"
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HAA0401", Justification = "Official IChatClient contract accepts IEnumerable<ChatMessage>.")]
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string systemPrompt = string.Empty;
        string userPrompt = string.Empty;

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
                systemPrompt = msg.Text ?? string.Empty;
            else if (msg.Role == ChatRole.User)
                userPrompt = msg.Text ?? string.Empty;
        }

        string modelId = options?.ModelId ?? "domain-adapter";
        await foreach (var token in _llmService.GenerateStreamAsync(systemPrompt, userPrompt, cancellationToken))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, token)
            {
                ModelId = modelId
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
            return this;
        if (serviceKey is null && serviceType.IsInstanceOfType(_llmService))
            return _llmService;
        return null;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Adapts the official Microsoft.Extensions.AI.IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt; to the Domain IEmbeddingService port.
/// </summary>
public sealed class MeaiEmbeddingServiceAdapter : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public MeaiEmbeddingServiceAdapter(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    public async Task<float[]> GenerateEmbeddingAsync(ReadOnlyMemory<char> text, CancellationToken cancellationToken = default)
    {
        var results = await _generator.GenerateAsync(new[] { text.Span.ToString() }, options: null, cancellationToken);
        if (results.Count == 0)
            return Array.Empty<float>();

        return results[0].Vector.ToArray();
    }

    public async Task<List<float[]>> GenerateEmbeddingsBatchAsync(
        IReadOnlyList<ReadOnlyMemory<char>> texts,
        CancellationToken cancellationToken = default)
    {
        var inputs = new string[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            inputs[i] = texts[i].Span.ToString();
        }

        var generated = await _generator.GenerateAsync(inputs, options: null, cancellationToken);
        var list = new List<float[]>(generated.Count);
        for (int i = 0; i < generated.Count; i++)
        {
            list.Add(generated[i].Vector.ToArray());
        }
        return list;
    }
}

/// <summary>
/// Adapts Domain IEmbeddingService to the official Microsoft.Extensions.AI.IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt; contract.
/// </summary>
public sealed class DomainToMeaiEmbeddingGeneratorAdapter : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingService _embeddingService;

    public DomainToMeaiEmbeddingGeneratorAdapter(IEmbeddingService embeddingService)
    {
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HAA0401", Justification = "Official IEmbeddingGenerator contract accepts IEnumerable<string>.")]
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            float[] vector = await _embeddingService.GenerateEmbeddingAsync(value.AsMemory(), cancellationToken);
            results.Add(new Embedding<float>(vector)
            {
                ModelId = options?.ModelId ?? "domain-embedding-adapter"
            });
        }
        return results;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
            return this;
        if (serviceKey is null && serviceType.IsInstanceOfType(_embeddingService))
            return _embeddingService;
        return null;
    }

    public void Dispose()
    {
    }
}
