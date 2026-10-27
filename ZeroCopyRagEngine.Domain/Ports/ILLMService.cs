using System.Collections.Generic;
using System.Threading;

namespace ModernAiAssistant.Domain.Ports;

public interface ILLMService
{
    /// <summary>
    /// Streams tokens asynchronously to prevent LOH (Large Object Heap) fragmentation.
    /// </summary>
    IAsyncEnumerable<string> GenerateStreamAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}
