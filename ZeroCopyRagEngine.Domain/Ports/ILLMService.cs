using System.Collections.Generic;
using System.Threading;

namespace ZeroCopyRagEngine.Domain.Ports;

public interface ILLMService
{
    /// <summary>
    /// Streams generated tokens asynchronously to minimize time-to-first-token (TTFT) and avoid buffering the full response.
    /// </summary>
    IAsyncEnumerable<string> GenerateStreamAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}
