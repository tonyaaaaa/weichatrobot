namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeOcrPageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KnowledgeDocumentVersionId { get; set; }
    public int PageNumber { get; set; }
    public string Status { get; set; } = "processing";
    public string BlocksJson { get; set; } = "[]";
    public string? Error { get; set; }
    public int AttemptCount { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
