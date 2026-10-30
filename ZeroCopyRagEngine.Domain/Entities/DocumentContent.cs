using System;
using System.Collections.Generic;

namespace ZeroCopyRagEngine.Domain.Entities;

public sealed record PageContent(
    int PageNumber,
    ReadOnlyMemory<char> RawText,
    string? SectionHeader = null);

public sealed record DocumentContent(
    string FileId,
    string FileName,
    IReadOnlyList<PageContent> Pages,
    string? Source = null,
    DateTimeOffset? CreatedAt = null,
    IReadOnlyList<string>? Tags = null);
