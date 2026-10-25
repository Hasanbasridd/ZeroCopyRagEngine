# 🦅 Ultra-Performance Zero-Copy RAG Engine (.NET 9 / C# 13)

[![CI](https://github.com/Hasanbasridd/ZeroCopyRagEngine/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hasanbasridd/ZeroCopyRagEngine/actions/workflows/ci.yml)
[![Framework](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![Language](https://img.shields.io/badge/C%23-13.0-blue.svg)](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-13)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![GC Allocation](https://img.shields.io/badge/GC%20Allocation-see%20budget-yellow.svg)](#-allocation-budget)

A Retrieval-Augmented Generation (RAG) pipeline built with **Clean Architecture** principles and modern .NET performance primitives (`Span<T>`, `ArrayPool<T>`, `System.Numerics.Tensors`). Benchmark figures are pending re-measurement (see [caveats](#-benchmark-results-benchmarkdotnet)); exactly where allocations do and do not happen is documented in the [Allocation Budget](#-allocation-budget).

---

## ⚡ Core Architectural Pillars & Performance Engineering

This project is not just another RAG wrapper; it is an exploration of high-performance systems engineering using modern .NET primitives.

* **Low-Allocation Chunking:** [`ZeroCopySemanticOverlapChunker`](ModernAiAssistant.Infrastructure/Chunkers/ZeroCopySemanticOverlapChunker.cs) avoids `string.Split()`, string concatenation and LINQ. Chunk boundaries are computed on `ReadOnlySpan<char>` with a pooled `ArrayPool<Range>` buffer, and chunks are returned as `ReadOnlyMemory<char>` slices of the original in-memory string (no substring copies). The `yield`-based iterator allocates one state-machine object per call.
* **SIMD-Accelerated Vector Search:** [`SimdLocalVectorDatabase`](ModernAiAssistant.Infrastructure/VectorStores/SimdLocalVectorDatabase.cs) stores vectors in a single flattened in-memory `float[]` and scores them with `TensorPrimitives.CosineSimilarity`, which uses hardware intrinsics (AVX2 / AVX-512 / ARM NEON) when the CPU supports them. Search is an exact, single-threaded O(N) flat scan.
* **SSE Streaming:** An `IAsyncEnumerable<string>` pipeline streams LLM tokens to the HTTP response (`text/event-stream`) as they are produced, so the full answer is never buffered server-side. Each token is still encoded into a small per-token string/byte array.
* **The Cyber-Guard (Siber-Bekçi):** Allocation discipline for the hot paths (`Chunkers/` and `VectorStores/`) is enforced in three layers:
  1. **Roslyn analyzer** ([ClrHeapAllocationAnalyzer](https://github.com/microsoft/RoslynClrHeapAllocationAnalyzer)) — heap-allocation diagnostics are **build errors** in those folders ([`.editorconfig`](.editorconfig)). Intentional allocations require a `[SuppressMessage]` with a written justification.
  2. **CI** ([`.github/workflows/cyber-guard.yml`](.github/workflows/cyber-guard.yml)) — runs the same build on every push/PR, so it cannot be bypassed with `--no-verify`.
  3. **Versioned pre-commit hook** ([`.githooks/pre-commit`](.githooks/pre-commit)) — fast local feedback that rejects `.ToList()`, `.Select()` and `.Split()`.

---

## 🏛️ Clean Architecture Layering

The engine is split into four layer projects plus tests and benchmarks:

```text
ZeroCopyRagEngine/
├── ModernAiAssistant.RagEngine.sln
├── ModernAiAssistant.Domain/          # Entities (TextChunk, ScoredChunk, ...) and Ports (IVectorDatabase, ITextChunker, ILLMService, IEmbeddingService, IDocumentReader)
├── ModernAiAssistant.Application/     # Use cases (ChatRetrievalService) and DTOs
├── ModernAiAssistant.Infrastructure/  # Chunkers/, VectorStores/, Embedding/, LLM/ adapters
├── ModernAiAssistant.Presentation/    # ASP.NET Core Web API (ChatController) + static UI in wwwroot/
├── ModernAiAssistant.Tests/           # Pipeline and presentation tests
├── ModernAiAssistant.Benchmarks/      # BenchmarkDotNet suites
├── docs/adr/                          # Architecture Decision Records
├── .githooks/                         # Versioned Cyber-Guard hook
├── .github/workflows/                 # CI (Cyber-Guard)
└── setup-hooks.sh                     # Activates .githooks via core.hooksPath
```

---

## 🚀 Benchmark Results (BenchmarkDotNet)

> [!NOTE]
> The numbers below come from an earlier run whose environment (CPU, RAM, OS, .NET build) was not recorded. They are kept for reference until the benchmarks are re-run with full environment info.

### 1. Text Chunking vs String Split
| Method | Mean | Allocated |
| :--- | :--- | :--- |
| `TraditionalStringSplit` (baseline) | 326.14 ms | 2.54 MB |
| **`ZeroCopySpanChunking`** (`ZeroCopySemanticOverlapChunker`) | **5.27 ms** | **0 B reported** |

Caveat: the baseline splits the text into words with `string.Split`, which is a weak comparison and not an equivalent chunking algorithm. The `0 B` figure must be re-verified, since the iterator allocates one state-machine object per call.

### 2. SIMD Vector Search (100,000 vectors, dimension 384, top-k 5)
| Search Target | Latency | Hardware Acceleration | Allocated |
| :--- | :--- | :--- | :--- |
| Cosine Similarity Search | < 1.2 ms (unverified) | AVX-512 Enabled | Not 0 B (see budget) |

Caveat: 100,000 × 384 × 4 bytes ≈ 154 MB per scan. Scanning that in 1.2 ms on one thread would need ≈ 128 GB/s of memory bandwidth, which is not physically plausible, so this latency figure is treated as unverified until re-measured.

---

## 📒 Allocation Budget

Where heap allocations happen on a single question → answer request (based on the current source code):

| Stage | Code path | Allocates? | What |
| :--- | :--- | :--- | :--- |
| Chunk boundary calculation | `ZeroCopySemanticOverlapChunker.ChunkText` loop | No | Uses pooled `ArrayPool<Range>` and span slicing |
| Chunk iteration | `ZeroCopySemanticOverlapChunker.ChunkText` | Yes, once per call | Iterator state machine |
| Chunk content | `TextChunk.Content` | No copy | `ReadOnlyMemory<char>` slice of the source string |
| Query embedding | `IEmbeddingService.GenerateEmbeddingAsync` | Yes | `float[]` per query (current impl: `DummyEmbeddingService`) |
| Similarity scoring | `SimdLocalVectorDatabase.SearchSimilarAsync` O(N) loop | No | Pooled `ArrayPool<float>` / `ArrayPool<int>` buffers |
| Top-k results | `SimdLocalVectorDatabase.SearchSimilarAsync` | Yes | One `List<ScoredChunk>` + top-k `ScoredChunk` objects, plus the `Task` result |
| Vector ingestion | `SimdLocalVectorDatabase.UpsertChunksAsync` | Yes | Flattened store is re-allocated and copied per upsert batch |
| Prompt construction | `ChatRetrievalService.AskQuestionStreamAsync` | Yes | `StringBuilder` + final prompt `string` (chunks become a string here) |
| LLM call | `ILLMService.GenerateStreamAsync` | Yes | One `string` per streamed token (current impl: `DummyLLMService`) |
| SSE write | `ChatController.AskStream` | Yes | Per-token `string` + UTF-8 `byte[]` |

The scope of the "zero-allocation" claim is therefore the **chunk boundary loop** and the **similarity scoring loop**, not the full request.

---

## 🧾 Claim Evidence

| Claim | Evidence |
| :--- | :--- |
| 61x faster chunking (unverified) | Benchmark ([`ChunkerBenchmarks.cs`](ModernAiAssistant.Benchmarks/ChunkerBenchmarks.cs)), compared against a `string.Split` baseline only |
| 0 B in the chunking loop | Code review + Roslyn analyzer; the benchmark-level `0 B` still needs re-verification |
| 0 B in the similarity scoring loop | Code review + Roslyn analyzer; the full search call allocates (see budget) |
| < 1.2 ms search over 100k vectors | Benchmark ([`VectorSearchBenchmarks.cs`](ModernAiAssistant.Benchmarks/VectorSearchBenchmarks.cs)), environment not recorded, physically implausible (see caveat) |
| SIMD acceleration | `TensorPrimitives.CosineSimilarity` in source; actual ISA depends on the host CPU |
| Provider agnostic | Ports in the Domain layer; only dummy LLM/embedding adapters are implemented today |

---

## 🛠️ Quick Start

### Prerequisites
* .NET 9.0 SDK or higher
* No LLM provider is required to run the current build: it ships with `DummyLLMService` and `DummyEmbeddingService`. Real providers (e.g. Ollama / OpenAI) are not implemented yet.

### Installation & Execution
1. Clone the repository:
```bash
git clone https://github.com/Hasanbasridd/ZeroCopyRagEngine.git
cd ZeroCopyRagEngine
```

2. Activate the Cyber-Guard pre-commit hooks:
```bash
chmod +x ./setup-hooks.sh
./setup-hooks.sh
```

3. Run the Presentation Web API:
```bash
dotnet run --project ModernAiAssistant.Presentation
```

Open your browser and navigate to `http://localhost:<port>` to access the Luxury Glassmorphism Cockpit UI.

---
*Developed with passion for extreme performance and clean craftsmanship. ⚔️🛡️*
