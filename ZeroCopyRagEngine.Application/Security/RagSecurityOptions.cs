namespace ZeroCopyRagEngine.Application.Security;

public sealed class RagSecurityOptions
{
    public const string SectionName = "Security";

    public int MaxQueryLength { get; set; } = 2000;
    public int MaxChunkCount { get; set; } = 1000;
    public long MaxUploadSizeBytes { get; set; } = 10 * 1024 * 1024; // 10 MB
    public int PermitLimitPerMinute { get; set; } = 60;
    public string? ApiKey { get; set; }
    public bool RequireAuthentication { get; set; } = false;
}

