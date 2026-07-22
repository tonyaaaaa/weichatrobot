namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeDocumentVersionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KnowledgeDocumentId { get; set; }
    public int Version { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string SafeFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public string? PublicUrl { get; set; }
    public string Status { get; set; } = "uploading";
    public string? FailureReason { get; set; }
    public byte[] StagedContent { get; set; } = [];
    public bool IsPublished { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
