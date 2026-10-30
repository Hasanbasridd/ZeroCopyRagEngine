# ADR-0002: Flattened in-memory vector store with SIMD flat search

- Status: Accepted
- Date: 2026-10-26

## Context
Retrieval needs cosine similarity between a query vector and every stored vector. Storing vectors as separate arrays scatters them in memory.

## Decision
`SimdLocalVectorDatabase` keeps all vectors in one contiguous `float[]` (row-major, fixed dimension) and scores them with `TensorPrimitives.CosineSimilarity`, which uses hardware intrinsics when available. Scores and indices use `ArrayPool<float>` / `ArrayPool<int>` buffers.

## Rationale
- One contiguous array gives sequential memory access, which suits hardware prefetching and SIMD loads better than many separately allocated arrays.
- `TensorPrimitives` provides vectorized cosine similarity in the BCL, avoiding a native dependency or hand-written intrinsics.
- An exact flat scan is the simplest correct baseline; an approximate index (e.g. HNSW) can be introduced later behind the same `IVectorDatabase` port if N grows.

## Consequences
- Exact (not approximate) results; contiguous, cache-friendly scan; the scoring loop does not allocate.
- Search is O(N) and single-threaded; all N scores are fully sorted rather than selecting top-k.
- Result materialization allocates a `List<ScoredChunk>` and top-k `ScoredChunk` objects.
- Each upsert re-allocates and copies the whole store.
- In-memory only: no persistence.
