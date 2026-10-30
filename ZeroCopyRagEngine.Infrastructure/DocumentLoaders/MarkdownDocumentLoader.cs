using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.DocumentLoaders;

/// <summary>
/// Loads Markdown (.md) files into section-aware PageContent slices preserving heading hierarchy.
/// </summary>
public sealed class MarkdownDocumentLoader : IDocumentLoader
{
    public bool CanHandle(string fileExtension)
        => string.Equals(fileExtension, ".md", StringComparison.OrdinalIgnoreCase)
           || string.Equals(fileExtension, "md", StringComparison.OrdinalIgnoreCase)
           || string.Equals(fileExtension, ".markdown", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentContent> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
        => LoadAsync(fileStream, fileName, source: fileName, cancellationToken);

    public async Task<DocumentContent> LoadAsync(Stream stream, string fileName, string? source = null, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        string fullMarkdown = await reader.ReadToEndAsync(cancellationToken);
        ReadOnlyMemory<char> memory = fullMarkdown.AsMemory();

        var pages = new List<PageContent>();
        int sectionStart = 0;
        int pageNum = 1;
        string currentHeader = Path.GetFileNameWithoutExtension(fileName);

        for (int i = 0; i < fullMarkdown.Length; i++)
        {
            bool isLineStart = i == 0 || fullMarkdown[i - 1] == '\n';
            if (isLineStart && fullMarkdown[i] == '#')
            {
                if (i > sectionStart)
                {
                    ReadOnlyMemory<char> slice = memory.Slice(sectionStart, i - sectionStart);
                    if (!slice.Span.IsWhiteSpace())
                    {
                        pages.Add(new PageContent(pageNum++, slice, currentHeader));
                    }
                }

                sectionStart = i;
                int lineEnd = fullMarkdown.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = fullMarkdown.Length;
                currentHeader = fullMarkdown.AsSpan(i, lineEnd - i).Trim("# \r\n").ToString();
            }
        }

        if (sectionStart < fullMarkdown.Length)
        {
            ReadOnlyMemory<char> tail = memory.Slice(sectionStart);
            if (!tail.Span.IsWhiteSpace())
            {
                pages.Add(new PageContent(pageNum, tail, currentHeader));
            }
        }

        if (pages.Count == 0)
        {
            pages.Add(new PageContent(1, memory, currentHeader));
        }

        return new DocumentContent(
            FileId: Path.GetFileNameWithoutExtension(fileName),
            FileName: fileName,
            Pages: pages,
            Source: source ?? fileName,
            CreatedAt: DateTimeOffset.UtcNow);
    }
}
