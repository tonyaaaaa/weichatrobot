namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class WorkToolOperationAuditEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OperatorName { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string SanitizedRequestJson { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Result { get; set; }
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
