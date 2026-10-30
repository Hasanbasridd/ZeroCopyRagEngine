using ZeroCopyRagEngine.Domain.Entities;

namespace ZeroCopyRagEngine.Application.DTOs;

public sealed record ChatQueryRequest(string Query, MetadataFilter? Filter = null);
