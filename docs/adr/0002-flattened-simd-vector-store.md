# ADR-0002: Flattened in-memory vector store with SIMD flat search

- Status: Accepted
- Date: 2026-10-26

## Context
Retrieval needs cosine similarity between a query vector and stored vectors. Storing vectors as separate `float[]` arrays scatters them in memory and causes pointer-chasing cache misses.

## Decision
`SimdLocalVectorDatabase` keeps all vectors in one contiguous `float[]` (row-major, fixed dimension), scores them with `TensorPrimitives.CosineSimilarity` (which uses AVX2 / AVX-512 / ARM NEON hardware intrinsics when supported), and maintains top-K results in-place via `TopKSelector` on `Span<float>` and `Span<int>`.

## Rationale
- One contiguous array gives sequential memory access, maximizing hardware prefetch efficiency and SIMD throughput.
- `TensorPrimitives.CosineSimilarity` provides hardware-accelerated vector math in the .NET BCL without external native libraries.
- In-place `TopKSelector` avoids sorting all $N$ scores ($O(N \log N)$) and eliminates `Comparer<float>` delegate allocations.

## Consequences
- Exact (1.000 recall) results; contiguous, cache-friendly scan; `SearchSimilarSpan` allocates 0 bytes end-to-end.
- Flat scan is $O(N)$ and memory-bandwidth bound (~153.6 MB per 100k × 384 scan); see ADR-0005 for `MemoryMappedFile`, `Int8` quantization, and `HNSW` graph indexing.
- `SearchSimilarAsync` allocates the returned `Task`, `List<ScoredChunk>`, and `topK` `ScoredChunk` records.
