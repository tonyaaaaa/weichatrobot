namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeCandidateEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HandoffCaseId { get; set; }
    public Guid QuestionMessageId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public Guid? KnowledgeDocumentVersionId { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
}
