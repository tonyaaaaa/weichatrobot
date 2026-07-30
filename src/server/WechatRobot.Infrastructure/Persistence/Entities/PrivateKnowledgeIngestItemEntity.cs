namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class PrivateKnowledgeIngestItemEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public int Sequence { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string ChangeKind { get; set; } = "New";
    public Guid? MatchedDocumentId { get; set; }
    public Guid? MatchedVersionId { get; set; }
    public Guid? StagedDocumentId { get; set; }
    public Guid? StagedVersionId { get; set; }
    public string QuestionFingerprint { get; set; } = string.Empty;
    public string AnswerFingerprint { get; set; } = string.Empty;
    public string ProposedTagsJson { get; set; } = "[]";
    public string ResolvedTagIdsJson { get; set; } = "[]";
    public string? FailureCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
