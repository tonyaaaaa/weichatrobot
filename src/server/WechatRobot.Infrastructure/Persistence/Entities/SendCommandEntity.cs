namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class SendCommandEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RobotConfigId { get; set; }
    public Guid? GroupProfileId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    public DateTime? ExternalDispatchStartedAtUtc { get; set; }
    public string? ReconciliationReason { get; set; }
    public string? WorkToolCommandMessageId { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public int? WorkToolResultCode { get; set; }
    public DateTime? WorkToolResultAtUtc { get; set; }
    public string? WorkToolSuccessListJson { get; set; }
    public string? WorkToolFailListJson { get; set; }
}
