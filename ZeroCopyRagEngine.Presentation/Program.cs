using System;
using System.Globalization;
using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroCopyRagEngine.Application.Security;
using ZeroCopyRagEngine.Application.Services;
using ZeroCopyRagEngine.Domain.Ports;
using ZeroCopyRagEngine.Infrastructure.BM25;
using ZeroCopyRagEngine.Infrastructure.Chunkers;
using ZeroCopyRagEngine.Infrastructure.DocumentLoaders;
using ZeroCopyRagEngine.Infrastructure.Embedding;
using ZeroCopyRagEngine.Infrastructure.LLM;
using ZeroCopyRagEngine.Infrastructure.Providers;
using ZeroCopyRagEngine.Infrastructure.Reranking;
using ZeroCopyRagEngine.Infrastructure.Resilience;
using ZeroCopyRagEngine.Infrastructure.VectorStores;
using ZeroCopyRagEngine.Presentation.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Bind Security Options (Environment Variables / User-Secrets take precedence over appsettings.json)
var securityOptions = new RagSecurityOptions();
builder.Configuration.GetSection(RagSecurityOptions.SectionName).Bind(securityOptions);
string? envApiKey = Environment.GetEnvironmentVariable("RAG_API_KEY");
if (!string.IsNullOrWhiteSpace(envApiKey))
{
    securityOptions.ApiKey = envApiKey;
}
builder.Services.AddSingleton(securityOptions);

// Bind AI Provider Options ("Dummy" | "Ollama" | "OpenAI")
var providerOptions = new AiProviderOptions();
builder.Configuration.GetSection(AiProviderOptions.SectionName).Bind(providerOptions);
string? envProviderKey = Environment.GetEnvironmentVariable("AI_PROVIDER_API_KEY");
if (!string.IsNullOrWhiteSpace(envProviderKey))
{
    providerOptions.ApiKey = envProviderKey;
}
builder.Services.AddSingleton(providerOptions);

// DI - Core RAG Infrastructure Components
builder.Services.AddSingleton<IVectorDatabase, SimdLocalVectorDatabase>();
builder.Services.AddSingleton<IBm25Index, Bm25InvertedIndex>();
builder.Services.AddSingleton<IReranker, LexicalCrossEncoderReranker>();
builder.Services.AddSingleton<ITextChunker, ZeroCopySemanticOverlapChunker>();
builder.Services.AddSingleton<IDocumentLoader, PlainTextDocumentLoader>();
builder.Services.AddSingleton<IDocumentLoader, MarkdownDocumentLoader>();
builder.Services.AddSingleton<IDocumentLoader, PdfDocumentLoader>();

// DI - Provider-Agnostic Microsoft.Extensions.AI & Domain Adapters with HTTP Resilience
if (string.Equals(providerOptions.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IChatClient>(_ =>
    {
        var handler = new HttpResilienceHandler(new ResiliencePipelineOptions(), new HttpClientHandler());
        var client = new HttpClient(handler) { BaseAddress = new Uri(providerOptions.Endpoint) };
        return new OllamaChatClient(client, providerOptions.ChatModel);
    });
    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
    {
        var handler = new HttpResilienceHandler(new ResiliencePipelineOptions(), new HttpClientHandler());
        var client = new HttpClient(handler) { BaseAddress = new Uri(providerOptions.Endpoint) };
        return new OllamaEmbeddingGenerator(client, providerOptions.EmbeddingModel);
    });
    builder.Services.AddSingleton<ILLMService, MeaiChatClientLlmAdapter>();
    builder.Services.AddSingleton<IEmbeddingService, MeaiEmbeddingServiceAdapter>();
}
else if (string.Equals(providerOptions.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IChatClient>(_ =>
    {
        var handler = new HttpResilienceHandler(new ResiliencePipelineOptions(), new HttpClientHandler());
        var client = new HttpClient(handler) { BaseAddress = new Uri(providerOptions.Endpoint) };
        if (!string.IsNullOrWhiteSpace(providerOptions.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", providerOptions.ApiKey);
        }
        return new OpenAiChatClient(client, providerOptions.ChatModel);
    });
    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
    {
        var handler = new HttpResilienceHandler(new ResiliencePipelineOptions(), new HttpClientHandler());
        var client = new HttpClient(handler) { BaseAddress = new Uri(providerOptions.Endpoint) };
        if (!string.IsNullOrWhiteSpace(providerOptions.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", providerOptions.ApiKey);
        }
        return new OpenAiEmbeddingGenerator(client, providerOptions.EmbeddingModel);
    });
    builder.Services.AddSingleton<ILLMService, MeaiChatClientLlmAdapter>();
    builder.Services.AddSingleton<IEmbeddingService, MeaiEmbeddingServiceAdapter>();
}
else
{
    builder.Services.AddSingleton<ILLMService, DummyLLMService>();
    builder.Services.AddSingleton<IEmbeddingService, DummyEmbeddingService>();
    builder.Services.AddSingleton<IChatClient, DomainToMeaiChatClientAdapter>();
    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, DomainToMeaiEmbeddingGeneratorAdapter>();
}

// DI - Application Layer
builder.Services.AddScoped<HybridRetrievalService>();
builder.Services.AddScoped<ChatRetrievalService>();

// ASP.NET Core Rate Limiting with Retry-After header
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
        }

        await context.HttpContext.Response.WriteAsync("Rate limit exceeded. Please retry later.", token);
    };
    options.AddPolicy("chat-limit", httpContext =>
    {
        var activeSecurity = httpContext.RequestServices.GetService<RagSecurityOptions>() ?? securityOptions;
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "local",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, activeSecurity.PermitLimitPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

// Liveness & Readiness Health Checks
builder.Services.AddHealthChecks()
    .AddCheck("vector_store_ready", () => HealthCheckResult.Healthy("Vector store and retrieval pipeline initialized."));

// CORS Policy for Cockpit UI
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowCockpitUI",
        policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

var app = builder.Build();

// Middleware Pipeline
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors("AllowCockpitUI");
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
app.UseRateLimiter();

// Serve the Luxury Dark Glassmorphism UI
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();
app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapHealthChecks("/ready");
app.MapControllers();

app.Run();

// Needed for Integration Tests (WebApplicationFactory)
public partial class Program { }
