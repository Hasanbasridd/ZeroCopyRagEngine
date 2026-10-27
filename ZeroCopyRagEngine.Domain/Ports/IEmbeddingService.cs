using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ModernAiAssistant.Domain.Ports;

public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(ReadOnlyMemory<char> text, CancellationToken cancellationToken = default);
    Task<List<float[]>> GenerateEmbeddingsBatchAsync(IReadOnlyList<ReadOnlyMemory<char>> texts, CancellationToken cancellationToken = default);
}
