namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeDocumentEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "uploading";
    public Guid? ActiveVersionId { get; set; }
    public string? ActiveCollectionName { get; set; }
    public int? ActiveEmbeddingDimension { get; set; }
    public string? ActiveDistance { get; set; }
    public int? ActiveIndexGeneration { get; set; }
    public bool ActiveCollectionExclusive { get; set; }
    public bool IsDeleteRequested { get; set; }
    public int StateVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
