namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class GroupHumanAgentEntity
{
    public Guid GroupProfileId { get; set; }
    public Guid ApplicationUserId { get; set; }
    public string WorkToolDisplayNameSnapshot { get; set; } = string.Empty;
    public DateTime? LastVerifiedAtUtc { get; set; }
    public string VerificationStatus { get; set; } = "Stale";
    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
