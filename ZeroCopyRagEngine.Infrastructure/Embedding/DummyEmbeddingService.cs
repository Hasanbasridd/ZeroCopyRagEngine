using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ModernAiAssistant.Domain.Ports;

namespace ModernAiAssistant.Infrastructure.Embedding;

public sealed class DummyEmbeddingService : IEmbeddingService
{
    public Task<float[]> GenerateEmbeddingAsync(ReadOnlyMemory<char> text, CancellationToken cancellationToken = default)
    {
        var random = new Random();
        var vec = new float[384];
        for (int i = 0; i < 384; i++) vec[i] = (float)random.NextDouble();
        return Task.FromResult(vec);
    }

    public Task<List<float[]>> GenerateEmbeddingsBatchAsync(IReadOnlyList<ReadOnlyMemory<char>> texts, CancellationToken cancellationToken = default)
    {
        var list = new List<float[]>();
        foreach(var t in texts)
        {
            list.Add(GenerateEmbeddingAsync(t, cancellationToken).Result);
        }
        return Task.FromResult(list);
    }
}
