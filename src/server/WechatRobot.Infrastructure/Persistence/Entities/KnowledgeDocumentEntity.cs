namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeDocumentEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "uploading";
    public Guid? ActiveVersionId { get; set; }
    public bool IsDeleteRequested { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
