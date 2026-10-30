using System;
using System.Collections.Generic;

namespace ZeroCopyRagEngine.Domain.Entities;

public sealed record DocumentMetadata(
    string FileId,
    string FileName,
    int PageNumber,
    int ChunkIndex,
    string? Source = null,
    DateTimeOffset? CreatedAt = null,
    IReadOnlyList<string>? Tags = null);
