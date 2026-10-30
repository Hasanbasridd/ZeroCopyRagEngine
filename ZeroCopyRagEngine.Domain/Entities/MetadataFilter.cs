using System;
using System.Collections.Generic;

namespace ZeroCopyRagEngine.Domain.Entities;

public sealed record MetadataFilter(
    string? FileId = null,
    string? Source = null,
    DateTimeOffset? MinCreatedAt = null,
    DateTimeOffset? MaxCreatedAt = null,
    IReadOnlyList<string>? RequiredTags = null)
{
    public bool Matches(DocumentMetadata metadata)
    {
        if (!string.IsNullOrEmpty(FileId) && !string.Equals(metadata.FileId, FileId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(Source) && !string.Equals(metadata.Source, Source, StringComparison.OrdinalIgnoreCase))
            return false;

        if (MinCreatedAt.HasValue)
        {
            if (!metadata.CreatedAt.HasValue || metadata.CreatedAt.Value < MinCreatedAt.Value)
                return false;
        }

        if (MaxCreatedAt.HasValue)
        {
            if (!metadata.CreatedAt.HasValue || metadata.CreatedAt.Value > MaxCreatedAt.Value)
                return false;
        }

        if (RequiredTags is { Count: > 0 })
        {
            if (metadata.Tags is null || metadata.Tags.Count == 0)
                return false;

            for (int i = 0; i < RequiredTags.Count; i++)
            {
                string req = RequiredTags[i];
                bool found = false;
                for (int j = 0; j < metadata.Tags.Count; j++)
                {
                    if (string.Equals(metadata.Tags[j], req, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return false;
            }
        }

        return true;
    }
}

