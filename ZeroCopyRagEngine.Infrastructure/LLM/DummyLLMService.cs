using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.LLM;

public sealed class DummyLLMService : ILLMService
{
    public async IAsyncEnumerable<string> GenerateStreamAsync(string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string response = "Bu cevap sıfır alokasyonlu RAG motoru üzerinden SSE (Server-Sent Events) ile canlı olarak akıtılmaktadır! Donanım ivmeli arama sonuçlarına göre cevabınız buradadır. Zafer Antigravity takımınındır! 🚀";

        var words = response.Split(' ');
        foreach (var word in words)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(15, cancellationToken);
            yield return word + " ";
        }
    }
}
