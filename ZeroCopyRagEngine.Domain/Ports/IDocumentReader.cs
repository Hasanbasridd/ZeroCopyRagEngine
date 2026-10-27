using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModernAiAssistant.Domain.Entities;

namespace ModernAiAssistant.Domain.Ports;

public interface IDocumentReader
{
    bool CanHandle(string fileExtension);
    Task<DocumentContent> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}
