namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class RobotConfigEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string WorkToolRobotId { get; set; } = string.Empty;
    public string? EncryptedWorkToolRobotId { get; set; }
    public string? CallbackRouteCode { get; set; }
    public string CallbackSecretHash { get; set; } = string.Empty;
    public string? EncryptedCallbackSecret { get; set; }
    public string? PreviousCallbackSecretHash { get; set; }
    public DateTime? PreviousCallbackSecretExpiresAtUtc { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SendRateLimitPerMinute { get; set; } = 50;
    public decimal SendRateTokens { get; set; } = 50m;
    public DateTime SendRateUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? SendLeaseOwner { get; set; }
    public DateTime? SendLeaseExpiresAtUtc { get; set; }
    public int SendCoordinationVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
