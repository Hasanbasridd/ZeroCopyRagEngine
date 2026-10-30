# ADR-0005: Memory-mapped persistence, Int8 quantization, HNSW, and Hybrid RRF retrieval

- Status: Accepted
- Date: 2026-10-30

## Context
In-memory flat `float32` vector search alone has three limitations: (1) vectors are lost on process restart, (2) $O(N)$ scans become memory-bandwidth bound at large $N$, and (3) dense embeddings alone can miss exact keyword/identifier matches.

## Decision
1. **Zero-Copy Persistence (`MemoryMappedVectorDatabase`):** Persist contiguous `float32` vectors after a 16-byte `ZCRV` header and map the file via `MemoryMappedFile` + `SafeMemoryMappedViewHandle.AcquirePointer` directly as a `ReadOnlySpan<float>` over the OS page cache.
2. **Compression (`Int8QuantizedVectorDatabase`):** Quantize `float32` vectors to `sbyte[-127..127]` with per-vector scaling ($4\times$ memory reduction).
3. **Sub-linear ANN (`HnswVectorDatabase`):** Provide a multi-layer navigable small world graph index (`M`, `efConstruction`, `efSearch`) behind `IVectorDatabase`.
4. **Hybrid Search (`HybridRetrievalService`):** Combine dense vector search with `Bm25InvertedIndex` ($k_1 = 1.5, b = 0.75$) via `ReciprocalRankFusion` ($k = 60$) and refine top candidates with `LexicalCrossEncoderReranker`.

## Rationale
- Reading vectors via `MemoryMappedFile` extends the project's zero-copy design from in-memory text chunking to disk-backed vector retrieval.
- Combining BM25 lexical signals with dense embeddings via RRF eliminates score-calibration issues and improves retrieval MRR and nDCG on technical queries.

## Consequences
- Callers can choose between exact flat SIMD, zero-copy memory-mapped persistence, 4x-compressed `int8`, or sub-linear HNSW behind the same `IVectorDatabase` port.
- `MemoryMappedVectorDatabase` uses `unsafe` pointer acquisition scoped strictly inside `SearchSimilarAsync` and `UpsertChunksAsync` after validating file headers.

