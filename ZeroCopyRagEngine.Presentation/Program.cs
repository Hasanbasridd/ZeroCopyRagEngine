using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModernAiAssistant.Application.Services;
using ModernAiAssistant.Domain.Ports;
using ModernAiAssistant.Infrastructure.Chunkers;
using ModernAiAssistant.Infrastructure.Embedding;
using ModernAiAssistant.Infrastructure.LLM;
using ModernAiAssistant.Infrastructure.VectorStores;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// DI - Infrastructure Layer (Hardcore Performance Components)
builder.Services.AddSingleton<IVectorDatabase, SimdLocalVectorDatabase>();
builder.Services.AddSingleton<ITextChunker, ZeroCopySemanticOverlapChunker>();
builder.Services.AddSingleton<ILLMService, DummyLLMService>();
builder.Services.AddSingleton<IEmbeddingService, DummyEmbeddingService>();

// DI - Application Layer
builder.Services.AddScoped<ChatRetrievalService>();

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

// Configure the HTTP request pipeline.
app.UseCors("AllowCockpitUI");

// Serve the Luxury Dark Glassmorphism UI
app.UseDefaultFiles();
app.UseStaticFiles(); 

app.UseAuthorization();
app.MapControllers();

app.Run();

// Needed for Integration Tests (WebApplicationFactory)
public partial class Program { }
