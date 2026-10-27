using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ModernAiAssistant.Domain.Ports;

namespace ModernAiAssistant.Infrastructure.LLM;

public sealed class DummyLLMService : ILLMService
{
    public async IAsyncEnumerable<string> GenerateStreamAsync(string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string response = "Bu cevap sıfır alokasyonlu RAG motoru üzerinden SSE (Server-Sent Events) ile canlı olarak akıtılmaktadır! Donanım ivmeli arama sonuçlarına göre cevabınız buradadır. Zafer Antigravity takımınındır! 🚀";
        
        var words = response.Split(' ');
        foreach (var word in words)
        {
            await Task.Delay(50, cancellationToken); // Simüle edilmiş token gecikmesi
            yield return word + " ";
        }
    }
}
