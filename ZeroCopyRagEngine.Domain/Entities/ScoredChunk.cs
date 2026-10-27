namespace ZeroCopyRagEngine.Domain.Entities;

public sealed record ScoredChunk(TextChunk Chunk, float SimilarityScore);
