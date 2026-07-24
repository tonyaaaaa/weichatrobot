namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeChunkEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KnowledgeDocumentVersionId { get; set; }
    public int Sequence { get; set; }
    public int? PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public string HeadingsJson { get; set; } = "[]";
    public bool IsTable { get; set; }
    public int? TableRows { get; set; }
    public int? TableColumns { get; set; }
    public string? Question { get; set; }
    public string SynonymsJson { get; set; } = "[]";
    public string? Answer { get; set; }
    public string Status { get; set; } = "draft";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
