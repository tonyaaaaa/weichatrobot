namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeChunkEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KnowledgeDocumentVersionId { get; set; }
    public int Sequence { get; set; }
    public int? PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
