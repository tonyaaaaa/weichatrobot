namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class DurableJobEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JobType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public Guid? RelatedConversationMessageId { get; set; }
    public Guid? GroupProfileId { get; set; }
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTime AvailableAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
