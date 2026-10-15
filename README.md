# 🦅 Ultra-Performance Zero-Copy RAG Engine (.NET 9 / C# 13)

[![CI](https://github.com/Hasanbasridd/ZeroCopyRagEngine/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hasanbasridd/ZeroCopyRagEngine/actions/workflows/ci.yml)
[![Framework](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![Language](https://img.shields.io/badge/C%23-13.0-blue.svg)](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-13)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![GC Allocation](https://img.shields.io/badge/GC%20Allocation-0--Byte-brightgreen.svg)](#)

An enterprise-grade, ultra-fast, and fully agnostic Retrieval-Augmented Generation (RAG) pipeline built with **Clean Architecture** principles. Designed specifically to exploit hardware-level optimizations, achieving up to **61x performance gains** with **0-Byte GC Allocation** on critical execution paths.

---

## ⚡ Core Architectural Pillars & Performance Engineering

This project is not just another RAG wrapper; it is an exploration of high-performance systems engineering using modern .NET primitives.

* **0-Byte GC Allocation Chunking:** Replaces standard memory-heavy operations like `string.Split()`, string concatenation (`+`), or heavy LINQ loops with modern .NET 9 primitives: `ReadOnlySpan<char>`, `ReadOnlyMemory<char>`, and `ArrayPool<Range>`. Text slicing happens directly on mapped buffers with absolute zero heap allocation overhead.
* **SIMD-Accelerated Vector Space:** Local vector similarity and semantic search pipelines utilize `.NET 9 System.Numerics.Tensors` (`TensorPrimitives.CosineSimilarity`). Calculations execute directly on CPU registers using hardware-accelerated instructions (AVX-512 / ARM Neon).
* **Non-Blocking SSE Streaming:** Leverages native `IAsyncEnumerable<string>` pipelines to yield LLM and similarity tokens directly to the HTTP response body (`text/event-stream`) in real-time, completely bypassing Large Object Heap (LOH) fragmentation.
* **The Cyber-Guard (Siber-Bekçi):** A strict custom Git pre-commit hook acts as an autonomous quality gate. It scans the hot paths (`Chunkers/` and `VectorStores/`) and blocks any commit containing heap-allocating methods like `.ToList()`, `.Select()`, or `string.Split()`.

---

## 🏛️ Clean Architecture Layering

The engine enforces a strict 4-layer architectural boundary to ensure complete database, model, and provider agnosticism:

```text
ModernAiAssistant.RagEngine/
├── 1. Domain/         # Pure domain records, entities, and Ports (Contracts)
├── 2. Application/    # Ingestion and Retrieval orchestrators (Use Cases)
├── 3. Infrastructure/ # SIMD Vector Database, Zero-Copy Chunkers, LLM Adapters
└── 4. Presentation/   # ASP.NET Core Web API & Apple Luxury Glassmorphism UI
```

---

## 🚀 Benchmark Results (BenchmarkDotNet)

### 1. Zero-Copy Text Chunking vs Traditional String Split
| Method | Mean | Allocated |
| :--- | :--- | :--- |
| Traditional String Split | 326.14 ms | 2.54 MB |
| **ZeroCopySpanChunker (Ours)** | **5.27 ms** | **0 B (Zero Allocation)** |

### 2. SIMD Hardware-Accelerated Vector Search (100,000 Vectors)
| Search Target | Latency | Hardware Acceleration | Allocated |
| :--- | :--- | :--- | :--- |
| Cosine Similarity Search | < 1.2 ms | AVX-512 Enabled | 0 B |

---

## 🛠️ Quick Start

### Prerequisites
* .NET 9.0 SDK or higher
* Local Ollama instance running (or OpenAI API Key configured)

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
