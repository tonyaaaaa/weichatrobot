namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class WorkToolOperationAuditEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OperatorName { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public int WorkToolCommandNumber { get; set; }
    public string SanitizedRequestJson { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Result { get; set; }
    public Guid? RobotConfigId { get; set; }
    public string? EncryptedCommandJson { get; set; }
    public int AttemptCount { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public DateTime? ExternalDispatchStartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? WorkToolCommandMessageId { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public int? WorkToolResultCode { get; set; }
    public DateTime? WorkToolResultAtUtc { get; set; }
    public string? WorkToolSuccessListJson { get; set; }
    public string? WorkToolFailListJson { get; set; }
    public string? ReconciliationStatus { get; set; }
    public int ReconciliationAttemptCount { get; set; }
    public DateTime? ReconciliationNextAttemptAtUtc { get; set; }
    public Guid? ReconciledGroupProfileId { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WorkToolOperationConfirmationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
