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
/// Lightweight PDF text loader extracting text operators (Tj / TJ) per page without native binary dependencies.
/// </summary>
public sealed class PdfDocumentLoader : IDocumentLoader
{
    public bool CanHandle(string fileExtension)
        => string.Equals(fileExtension, ".pdf", StringComparison.OrdinalIgnoreCase)
           || string.Equals(fileExtension, "pdf", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentContent> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
        => LoadAsync(fileStream, fileName, source: fileName, cancellationToken);

    public async Task<DocumentContent> LoadAsync(Stream stream, string fileName, string? source = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        string raw = Encoding.Latin1.GetString(ms.ToArray());

        var pages = new List<PageContent>();
        if (raw.StartsWith("%PDF", StringComparison.Ordinal))
        {
            var sb = new StringBuilder();
            int pageNumber = 1;
            int idx = 0;

            while (idx < raw.Length)
            {
                int bt = raw.IndexOf("BT", idx, StringComparison.Ordinal);
                if (bt < 0)
                    break;

                int et = raw.IndexOf("ET", bt + 2, StringComparison.Ordinal);
                if (et < 0)
                    et = raw.Length;

                ExtractParenthesizedStrings(raw.AsSpan(bt, et - bt), sb);
                if (sb.Length > 0)
                {
                    pages.Add(new PageContent(pageNumber++, sb.ToString().AsMemory()));
                    sb.Clear();
                }

                idx = et + 2;
            }
        }

        if (pages.Count == 0)
        {
            string utf8Text = Encoding.UTF8.GetString(ms.ToArray());
            pages.Add(new PageContent(1, utf8Text.AsMemory()));
        }

        return new DocumentContent(
            FileId: Path.GetFileNameWithoutExtension(fileName),
            FileName: fileName,
            Pages: pages,
            Source: source ?? fileName,
            CreatedAt: DateTimeOffset.UtcNow);
    }

    private static void ExtractParenthesizedStrings(ReadOnlySpan<char> block, StringBuilder output)
    {
        int i = 0;
        while (i < block.Length)
        {
            if (block[i] == '(')
            {
                int depth = 1;
                i++;
                while (i < block.Length && depth > 0)
                {
                    char c = block[i];
                    if (c == '\\' && i + 1 < block.Length)
                    {
                        output.Append(block[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '(') depth++;
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            output.Append(' ');
                            i++;
                            break;
                        }
                    }
                    if (depth > 0)
                        output.Append(c);
                    i++;
                }
            }
            else
            {
                i++;
            }
        }
    }
}

