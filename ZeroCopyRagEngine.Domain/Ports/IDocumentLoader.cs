using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;

namespace ZeroCopyRagEngine.Domain.Ports;

/// <summary>
/// Port for format-aware document loading (PlainText, Markdown, PDF) into structured pages.
/// </summary>
public interface IDocumentLoader : IDocumentReader
{
    Task<DocumentContent> LoadAsync(Stream stream, string fileName, string? source = null, CancellationToken cancellationToken = default);
}

