using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;
using ZeroCopyRagEngine.Infrastructure.Providers;
using ZeroCopyRagEngine.Infrastructure.Resilience;

namespace ZeroCopyRagEngine.Tests;

public class SecurityAndResilienceTests
{
    [Fact]
    public async Task Ollama_And_OpenAI_Clients_Should_Handle_Success_Retry_Timeout_And_Streaming_Cancellation()
    {
        // 1. OllamaChatClient: transient 429 then streaming NDJSON success
        int ollamaChatAttempts = 0;
        var ollamaChatHandler = new FakeHttpMessageHandler((req, _) =>
        {
            ollamaChatAttempts++;
            if (ollamaChatAttempts == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

            string ndjson =
                "{\"message\":{\"content\":\"Zero-\"},\"done\":false}\n" +
                "{\"message\":{\"content\":\"Copy!\"},\"done\":true}\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            });
        });

        var resilienceOpts = new ResiliencePipelineOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(2),
            MaxRetries = 2,
            BaseRetryDelay = TimeSpan.FromMilliseconds(5),
            CircuitBreakerFailureThreshold = 4,
            CircuitBreakerBreakDuration = TimeSpan.FromSeconds(5)
        };

        using var ollamaHttp = new HttpClient(new HttpResilienceHandler(resilienceOpts, ollamaChatHandler))
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
        using var ollamaChat = new OllamaChatClient(ollamaHttp, "llama3.2");
        ollamaChat.GetService(typeof(IChatClient)).Should().BeSameAs(ollamaChat);

        var chatResponse = await ollamaChat.GetResponseAsync(new[]
        {
            new ChatMessage(ChatRole.System, "You are a concise assistant."),
            new ChatMessage(ChatRole.User, "Say Zero-Copy!")
        });
        chatResponse.Text.Should().Be("Zero-Copy!");
        ollamaChatAttempts.Should().Be(2);

        // 2. OllamaEmbeddingGenerator: transient 500 then embedding array success
        int ollamaEmbAttempts = 0;
        var ollamaEmbHandler = new FakeHttpMessageHandler((_, _) =>
        {
            ollamaEmbAttempts++;
            if (ollamaEmbAttempts == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            string json = "{\"model\":\"nomic-embed-text\",\"embeddings\":[[0.25,0.5,0.75]]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        using var ollamaEmbHttp = new HttpClient(new HttpResilienceHandler(resilienceOpts, ollamaEmbHandler))
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
        using var ollamaEmb = new OllamaEmbeddingGenerator(ollamaEmbHttp, "nomic-embed-text");
        ollamaEmb.GetService(typeof(IEmbeddingGenerator<string, Embedding<float>>)).Should().BeSameAs(ollamaEmb);

        var meaiEmbAdapter = new MeaiEmbeddingServiceAdapter(ollamaEmb);
        float[] singleVec = await meaiEmbAdapter.GenerateEmbeddingAsync("test".AsMemory());
        singleVec.Should().Equal(0.25f, 0.5f, 0.75f);
        ollamaEmbAttempts.Should().Be(2);

        var batchVecs = await meaiEmbAdapter.GenerateEmbeddingsBatchAsync(new[] { "a".AsMemory() });
        batchVecs.Should().ContainSingle();

        // 3. OpenAiChatClient: SSE streaming + mid-stream cancellation
        var openAiChatHandler = new FakeHttpMessageHandler((_, _) =>
        {
            string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello \"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"OpenAI!\"}}]}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        });

        using var openAiHttp = new HttpClient(new HttpResilienceHandler(resilienceOpts, openAiChatHandler))
        {
            BaseAddress = new Uri("https://api.openai.com")
        };
        using var openAiChat = new OpenAiChatClient(openAiHttp, "gpt-4o-mini");
        openAiChat.GetService(typeof(IChatClient)).Should().BeSameAs(openAiChat);

        var openAiResp = await openAiChat.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Hi") });
        openAiResp.Text.Should().Be("Hello OpenAI!");

        // Mid-stream cancellation test
        using var cts = new CancellationTokenSource();
        var receivedTokens = new List<string>();
        Func<Task> cancelledStream = async () =>
        {
            await foreach (var update in openAiChat.GetStreamingResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "Hi") },
                cancellationToken: cts.Token))
            {
                receivedTokens.Add(update.Text);
                cts.Cancel();
            }
        };
        await cancelledStream.Should().ThrowAsync<OperationCanceledException>();
        receivedTokens.Should().ContainSingle().Which.Should().Be("Hello ");

        // 4. OpenAiEmbeddingGenerator: success
        var openAiEmbHandler = new FakeHttpMessageHandler((_, _) =>
        {
            string json = "{\"data\":[{\"embedding\":[0.1,0.2,0.3,0.4]}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        using var openAiEmbHttp = new HttpClient(new HttpResilienceHandler(resilienceOpts, openAiEmbHandler))
        {
            BaseAddress = new Uri("https://api.openai.com")
        };
        using var openAiEmb = new OpenAiEmbeddingGenerator(openAiEmbHttp, "text-embedding-3-small");
        openAiEmb.GetService(typeof(IEmbeddingGenerator<string, Embedding<float>>)).Should().BeSameAs(openAiEmb);

        var openAiEmbeddings = await openAiEmb.GenerateAsync(new[] { "hello" });
        openAiEmbeddings.Should().ContainSingle();
        openAiEmbeddings[0].Vector.ToArray().Should().Equal(0.1f, 0.2f, 0.3f, 0.4f);

        // 5. Timeout test via HttpResilienceHandler
        var slowHandler = new FakeHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var fastTimeoutOpts = new ResiliencePipelineOptions
        {
            RequestTimeout = TimeSpan.FromMilliseconds(30),
            MaxRetries = 0
        };
        using var timeoutHttp = new HttpClient(new HttpResilienceHandler(fastTimeoutOpts, slowHandler))
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
        using var timeoutChat = new OllamaChatClient(timeoutHttp, "llama3.2");
        Func<Task> timeoutCall = async () => await timeoutChat.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "slow") });
        await timeoutCall.Should().ThrowAsync<TimeoutException>();

        // 6. Official OllamaSharp client creation check
        using var officialOllama = AiProviderOptions.CreateOfficialOllamaClient(ollamaHttp, "llama3.2");
        officialOllama.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveOllama_SmokeTest_When_Local_Daemon_Is_Available()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var tagsRes = await httpClient.GetAsync("/api/tags");
            if (!tagsRes.IsSuccessStatusCode)
                return;
        }
        catch
        {
            return;
        }

        using var officialClient = AiProviderOptions.CreateOfficialOllamaClient(httpClient, "llama3.2");
        officialClient.Should().BeAssignableTo<IChatClient>();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
