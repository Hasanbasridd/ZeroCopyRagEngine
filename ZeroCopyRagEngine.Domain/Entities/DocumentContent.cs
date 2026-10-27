using System;
using System.Collections.Generic;

namespace ModernAiAssistant.Domain.Entities;

public sealed record PageContent(int PageNumber, ReadOnlyMemory<char> RawText);

public sealed record DocumentContent(string FileId, string FileName, IReadOnlyList<PageContent> Pages);
