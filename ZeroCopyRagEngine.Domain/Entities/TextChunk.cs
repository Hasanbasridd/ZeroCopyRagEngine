using System;

namespace ZeroCopyRagEngine.Domain.Entities;

public sealed record TextChunk(string ChunkId, ReadOnlyMemory<char> Content, float[] Embedding, DocumentMetadata Metadata);
