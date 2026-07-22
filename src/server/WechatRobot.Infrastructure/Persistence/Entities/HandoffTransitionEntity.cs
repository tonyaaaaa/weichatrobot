namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class HandoffTransitionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HandoffCaseId { get; set; }
    public Guid? ActorUserId { get; set; }
    public int Sequence { get; set; }
    public string FromState { get; set; } = string.Empty;
    public string ToState { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
