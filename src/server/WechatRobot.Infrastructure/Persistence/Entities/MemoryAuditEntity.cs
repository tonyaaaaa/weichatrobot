namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class MemoryAuditEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Action { get; set; } = string.Empty;
    public string ActorType { get; set; } = "system";
    public Guid? ActorUserId { get; set; }
    public string TargetType { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public int Version { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
