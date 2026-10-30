using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.DocumentLoaders;

public sealed class PlainTextDocumentLoader : IDocumentLoader
{
    public bool CanHandle(string fileExtension)
        => string.Equals(fileExtension, ".txt", StringComparison.OrdinalIgnoreCase)
           || string.Equals(fileExtension, "txt", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentContent> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
        => LoadAsync(fileStream, fileName, source: fileName, cancellationToken);

    public async Task<DocumentContent> LoadAsync(Stream stream, string fileName, string? source = null, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        string text = await reader.ReadToEndAsync(cancellationToken);

        var pages = new List<PageContent>
        {
            new(PageNumber: 1, RawText: text.AsMemory(), SectionHeader: Path.GetFileNameWithoutExtension(fileName))
        };

        return new DocumentContent(
            FileId: Path.GetFileNameWithoutExtension(fileName),
            FileName: fileName,
            Pages: pages,
            Source: source ?? fileName,
            CreatedAt: DateTimeOffset.UtcNow);
    }
}

