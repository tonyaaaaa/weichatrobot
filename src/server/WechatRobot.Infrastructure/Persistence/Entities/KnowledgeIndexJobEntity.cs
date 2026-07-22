namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeIndexJobEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KnowledgeDocumentId { get; set; }
    public Guid KnowledgeDocumentVersionId { get; set; }
    public Guid? PreviousActiveVersionId { get; set; }
    public string? PreviousActiveCollectionName { get; set; }
    public int? PreviousActiveEmbeddingDimension { get; set; }
    public string? PreviousActiveDistance { get; set; }
    public int Generation { get; set; } = 1;
    public string Operation { get; set; } = "index";
    public string CollectionName { get; set; } = string.Empty;
    public int Dimension { get; set; }
    public string Distance { get; set; } = "cosine";
    public string PendingTagIdsJson { get; set; } = "[]";
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
