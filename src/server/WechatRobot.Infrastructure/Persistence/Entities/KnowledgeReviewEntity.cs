namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeReviewEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KnowledgeCandidateId { get; set; }
    public Guid? ReviewerUserId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string TagIdsJson { get; set; } = "[]";
    public string? RevisedAnswer { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? RequestFingerprint { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
