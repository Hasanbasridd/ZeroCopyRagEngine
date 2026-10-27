using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;

namespace ZeroCopyRagEngine.Domain.Ports;

public interface IDocumentReader
{
    bool CanHandle(string fileExtension);
    Task<DocumentContent> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}
