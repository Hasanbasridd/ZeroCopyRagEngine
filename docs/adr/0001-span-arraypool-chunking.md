# ADR-0001: Span/ArrayPool-based text chunking

- Status: Accepted
- Date: 2026-10-26

## Context
Ingestion splits documents into overlapping chunks. Naive approaches (`string.Split`, `Substring`, LINQ) create one string per fragment and put pressure on the GC.

## Decision
`ZeroCopySemanticOverlapChunker` computes chunk boundaries on `ReadOnlySpan<char>`, stores intermediate boundaries in a pooled `ArrayPool<Range>` buffer, and returns chunks as `ReadOnlyMemory<char>` slices of the source text (`ChunkText`), or writes `Range` boundaries directly into a caller-supplied `Span<Range>` (`ChunkTextSpan`) with 0 B heap allocation. Boundaries prefer a sentence end (`.`), then a word boundary, within the last 30% of the window. Token size is approximated as 4 characters (with exact WordPiece token chunking provided by `TokenAwareZeroCopyChunker`).

## Rationale
- Slicing a `ReadOnlyMemory<char>` reuses the source buffer, so chunk content needs no per-chunk string copy.
- A pooled `Range` buffer (or caller-provided `Span<Range>`) avoids allocating a new boundary list on every call.
- Preferring sentence, then word boundaries keeps chunks semantically coherent without a tokenizer dependency.
- Alternatives (`string.Split`/`Substring`/LINQ) allocate one string per fragment, which is exactly the GC pressure described in the context.

## Consequences
- The boundary loop does not allocate; chunk content is not copied.
- The `yield`-based iterator (`ChunkText`) allocates one 72 B state-machine object per call, whereas `ChunkTextSpan` allocates 0 B.
- **Known Analyzer Limitation:** Roslyn `ClrHeapAllocationAnalyzer` does not inspect the compiler-generated state machine body of `yield return` iterator methods. To close this gap, `.githooks/pre-commit` and `.github/workflows/cyber-guard.yml` scan all files in `Chunkers/` and `VectorStores/` for `.Select(`, `.ToList(`, and `.Split(` (allowing `.NET 9` zero-allocation `ReadOnlySpan<char>.Split(` only when explicitly annotated with `// cyber-guard: allow-span-split (<reason>)`).
- Chunks hold a reference to the full source string, so the source stays alive as long as any chunk does.
