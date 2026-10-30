# ADR-0004: Clean Architecture boundaries and Microsoft.Extensions.AI adapters

- Status: Accepted
- Date: 2026-10-30

## Context
A production .NET RAG engine should integrate with the official `Microsoft.Extensions.AI` ecosystem (`IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`) and provider libraries (`OllamaSharp`, `Microsoft.Extensions.AI.OpenAI`) while keeping the core Domain model free of third-party NuGet churn.

## Decision
`ZeroCopyRagEngine.Domain` defines pure, zero-dependency ports (`ILLMService`, `IEmbeddingService`, `IVectorDatabase`, `ITextChunker`, `IDocumentLoader`, `IBm25Index`, `IReranker`). `ZeroCopyRagEngine.Infrastructure` references the official `Microsoft.Extensions.AI.Abstractions` (`10.8.0`), `Microsoft.Extensions.AI` (`10.8.0`), `Microsoft.Extensions.AI.OpenAI` (`10.8.0`), and `OllamaSharp` (`5.5.0`) NuGet packages and provides bidirectional adapters (`MeaiChatClientLlmAdapter`, `MeaiEmbeddingServiceAdapter`, `DomainToMeaiChatClientAdapter`, `DomainToMeaiEmbeddingGeneratorAdapter`) alongside `OllamaChatClient`, `OllamaEmbeddingGenerator`, `OpenAiChatClient`, and `OpenAiEmbeddingGenerator` protected by `HttpResilienceHandler`.

## Rationale
- Keeping `ZeroCopyRagEngine.Domain.csproj` free of external package references guarantees long-term stability of domain entities and ports.
- Referencing official `Microsoft.Extensions.AI` and `OllamaSharp` packages strictly in `ZeroCopyRagEngine.Infrastructure` avoids redefining standard contracts and allows drop-in composition with standard .NET AI middleware and DI containers.

## Consequences
- Switching between `Dummy`, `Ollama`, and `OpenAI` requires only changing `AiProvider:Provider` in configuration.
- Outbound HTTP calls are verified via stubbed `HttpMessageHandler` tests covering streaming responses, HTTP `429`/`500` retries, request timeouts, circuit breaking, and mid-stream `CancellationToken` cancellation.
