using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace ZeroCopyRagEngine.Infrastructure.Providers;

public sealed class AiProviderOptions
{
    public const string SectionName = "AiProvider";

    /// <summary>
    /// Supported values: "Dummy", "Ollama", "OpenAI"
    /// </summary>
    public string Provider { get; set; } = "Dummy";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string ChatModel { get; set; } = "llama3.2";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public string? ApiKey { get; set; }

    /// <summary>
    /// Creates an official OllamaSharp OllamaApiClient implementing IChatClient and IEmbeddingGenerator.
    /// </summary>
    public static OllamaApiClient CreateOfficialOllamaClient(HttpClient httpClient, string defaultModel = "llama3.2")
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        return new OllamaApiClient(httpClient, defaultModel);
    }
}

/// <summary>
/// Official Microsoft.Extensions.AI.IChatClient implementation for Ollama HTTP endpoints (/api/chat).
/// Supports HttpResilienceHandler pipelines for retry, timeout, and circuit-breaker testing.
/// </summary>
public sealed class OllamaChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OllamaChatClient(HttpClient httpClient, string model = "llama3.2")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
    }

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
            ModelId = options?.ModelId ?? _model
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HAA0401", Justification = "Official IChatClient contract accepts IEnumerable<ChatMessage>.")]
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string modelId = options?.ModelId ?? _model;
        var payload = new
        {
            model = modelId,
            stream = true,
            messages = BuildMessagesPayload(messages)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("message", out var msgElem) &&
                msgElem.TryGetProperty("content", out var contentElem))
            {
                string? text = contentElem.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text)
                    {
                        ModelId = modelId
                    };
                }
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HAA0401", Justification = "Official IChatClient contract accepts IEnumerable<ChatMessage>.")]
    private static List<object> BuildMessagesPayload(IEnumerable<ChatMessage> messages)
    {
        var list = new List<object>();
        foreach (var msg in messages)
        {
            list.Add(new { role = msg.Role.Value, content = msg.Text });
        }
        return list;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }
}

/// <summary>
/// Official Microsoft.Extensions.AI.IEmbeddingGenerator implementation for Ollama HTTP endpoints (/api/embed).
/// </summary>
public sealed class OllamaEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OllamaEmbeddingGenerator(HttpClient httpClient, string model = "nomic-embed-text")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string modelId = options?.ModelId ?? _model;
        var payload = new
        {
            model = modelId,
            input = values
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/embed")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var results = new GeneratedEmbeddings<Embedding<float>>();
        if (doc.RootElement.TryGetProperty("embeddings", out var embeddingsElem) &&
            embeddingsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var vecElem in embeddingsElem.EnumerateArray())
            {
                var vec = new float[vecElem.GetArrayLength()];
                int idx = 0;
                foreach (var num in vecElem.EnumerateArray())
                {
                    vec[idx++] = num.GetSingle();
                }
                results.Add(new Embedding<float>(vec) { ModelId = modelId });
            }
        }

        return results;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }
}

/// <summary>
/// Official Microsoft.Extensions.AI.IChatClient implementation for OpenAI-compatible HTTP endpoints (/v1/chat/completions).
/// </summary>
public sealed class OpenAiChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OpenAiChatClient(HttpClient httpClient, string model = "gpt-4o-mini")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
    }

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
            ModelId = options?.ModelId ?? _model
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HAA0401", Justification = "Official IChatClient contract accepts IEnumerable<ChatMessage>.")]
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string modelId = options?.ModelId ?? _model;
        var messagesPayload = new List<object>();
        foreach (var msg in messages)
        {
            messagesPayload.Add(new { role = msg.Role.Value, content = msg.Text });
        }

        var payload = new
        {
            model = modelId,
            stream = true,
            messages = messagesPayload
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            string json = line.Substring(6).Trim();
            if (json == "[DONE]")
                break;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var contentElem))
            {
                string? text = contentElem.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text)
                    {
                        ModelId = modelId
                    };
                }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }
}

/// <summary>
/// Official Microsoft.Extensions.AI.IEmbeddingGenerator implementation for OpenAI-compatible HTTP endpoints (/v1/embeddings).
/// </summary>
public sealed class OpenAiEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OpenAiEmbeddingGenerator(HttpClient httpClient, string model = "text-embedding-3-small")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _model = model;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string modelId = options?.ModelId ?? _model;
        var payload = new
        {
            model = modelId,
            input = values
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var results = new GeneratedEmbeddings<Embedding<float>>();
        if (doc.RootElement.TryGetProperty("data", out var dataElem) &&
            dataElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataElem.EnumerateArray())
            {
                if (item.TryGetProperty("embedding", out var vecElem))
                {
                    var vec = new float[vecElem.GetArrayLength()];
                    int idx = 0;
                    foreach (var num in vecElem.EnumerateArray())
                    {
                        vec[idx++] = num.GetSingle();
                    }
                    results.Add(new Embedding<float>(vec) { ModelId = modelId });
                }
            }
        }

        return results;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }
}
