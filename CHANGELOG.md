# Changelog

All notable changes to this project are documented in this file following [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/).

## [0.1.0] - 2026-10-30

### Added
- **Zero-Copy & Structure-Aware Chunking:** `ZeroCopySemanticOverlapChunker` (`ChunkText` and `ChunkTextSpan`), `TokenAwareZeroCopyChunker` (`Microsoft.ML.Tokenizers`), and `MarkdownHierarchyZeroCopyChunker`, verified across empty, single-long-word, Unicode/Turkish, and overlap clamp edge cases in `ChunkerTests.cs`.
- **Format-Aware Document Loaders:** `PlainTextDocumentLoader` (`.txt`), `MarkdownDocumentLoader` (`.md`), and `PdfDocumentLoader` (`.pdf`) implementing `IDocumentLoader`.
- **Persistence, Quantization & ANN Vector Stores:** `MemoryMappedVectorDatabase` (`MemoryMappedFile` OS page-cache zero-copy reads), `Int8QuantizedVectorDatabase` (4x memory compression), and `HnswVectorDatabase` (multi-layer navigable small world graph), verified in `VectorStoreAndHybridTests.cs`.
- **Hybrid Search & Reranking:** `Bm25InvertedIndex` ($k_1 = 1.5, b = 0.75$), `ReciprocalRankFusion` ($k = 60$), `LexicalCrossEncoderReranker`, and `MetadataFilter` (`FileId`, `Source`, `MinCreatedAt`, `MaxCreatedAt`, `RequiredTags`).
- **Golden Dataset & RAG Evaluation Engine:** 35-question golden dataset (`datasets/golden-dataset.json`) and `RagEvaluator` measuring `Recall@K`, `MRR@K`, `nDCG@K`, `Faithfulness`, and `AnswerRelevance` with CI regression gating.
- **Official `Microsoft.Extensions.AI` & `OllamaSharp` Integration:** Referenced official `Microsoft.Extensions.AI.Abstractions` (`10.8.0`), `Microsoft.Extensions.AI` (`10.8.0`), `Microsoft.Extensions.AI.OpenAI` (`10.8.0`), and `OllamaSharp` (`5.5.0`) in `ZeroCopyRagEngine.Infrastructure`, with `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` adapters and resilient HTTP clients (`OllamaChatClient`, `OllamaEmbeddingGenerator`, `OpenAiChatClient`, `OpenAiEmbeddingGenerator`) tested against simulated HTTP `200`/`429`/`500`/timeout/cancellation scenarios in `SecurityAndResilienceTests.cs`.
- **Observability, Resilience & Security:** OpenTelemetry `ActivitySource` and `Meter` (`RagTelemetry`), `CorrelationIdMiddleware`, `HttpResilienceHandler` (timeout, exponential retry, circuit breaker), `ApiKeyAuthenticationMiddleware`, ASP.NET Core Rate Limiting with `Retry-After` headers, `PromptSanitizer` tested against 19 indirect prompt injection patterns, `/health` & `/ready` probes, and `/openapi/v1.json`.
- **DevOps & Packaging:** Multi-stage `Dockerfile`, `docker-compose.yml` with Ollama, Coverlet code coverage in `.github/workflows/ci.yml` (`53` automated tests across `ChunkerTests.cs`, `VectorStoreAndHybridTests.cs`, `SecurityAndResilienceTests.cs`, `PresentationTests.cs`, and `RAGPipelineTests.cs`), manual `.github/workflows/benchmarks.yml`, and Architecture Decision Records (`ADR-0001` through `ADR-0005`).

### Changed
- Unified repository, solution (`ZeroCopyRagEngine.sln`), project names, and namespaces under `ZeroCopyRagEngine.*`.
- Replaced full $O(N \log N)$ `Array.Sort` + `Comparer<float>` allocation in `SimdLocalVectorDatabase` with allocation-free `TopKSelector` on `Span<float>` / `Span<int>`.
- Replaced earlier unverified `README.md` benchmark claims (`< 1.2 ms` single-threaded flat scan over 100,000 × 384 `float32` vectors, which would require ~128 GB/s single-core DRAM bandwidth for 153.6 MB per scan, as well as unrecorded-environment `61x` / `0 B` iterator claims) with recorded hardware measurements on i7-13700HX (`17.11 ms` / ~8.98 GB/s for 100k × 384 flat scan; `0.502 ms` and `Recall@10 = 0.500` for HNSW at $N = 10{,}000, D = 384, \text{efSearch} = 64$; `0 B` for `ChunkTextSpan` / `SearchSimilarSpan`).
