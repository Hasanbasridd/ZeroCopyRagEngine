namespace ModernAiAssistant.Domain.Entities;

public sealed record DocumentMetadata(string FileId, string FileName, int PageNumber, int ChunkIndex);
