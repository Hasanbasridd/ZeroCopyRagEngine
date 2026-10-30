using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ZeroCopyRagEngine.Application.Telemetry;

public static class RagTelemetry
{
    public const string ServiceName = "ZeroCopyRagEngine";
    public const string Version = "0.1.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, Version);
    public static readonly Meter Meter = new(ServiceName, Version);

    public static readonly Histogram<double> IngestionDurationMs = Meter.CreateHistogram<double>(
        "rag.ingestion.duration_ms",
        unit: "ms",
        description: "Duration of document chunking and vector ingestion in milliseconds.");

    public static readonly Histogram<double> RetrievalLatencyMs = Meter.CreateHistogram<double>(
        "rag.retrieval.latency_ms",
        unit: "ms",
        description: "Duration of vector and hybrid retrieval in milliseconds.");

    public static readonly Counter<long> StreamedTokensCounter = Meter.CreateCounter<long>(
        "rag.llm.streamed_tokens",
        unit: "{token}",
        description: "Total number of LLM tokens streamed over SSE.");

    public static readonly Counter<long> PromptInjectionSanitizedCounter = Meter.CreateCounter<long>(
        "rag.security.prompt_injection_sanitized",
        unit: "{event}",
        description: "Number of untrusted chunks where indirect prompt injection patterns were neutralized.");
}

