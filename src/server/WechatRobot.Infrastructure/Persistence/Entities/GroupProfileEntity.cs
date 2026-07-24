namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class GroupProfileEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RobotConfigId { get; set; }
    public string ExternalGroupId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool? ContextSenderIsolated { get; set; }
    public int? ContextHistoryTurns { get; set; }
    public int? ContextIdleTimeoutMinutes { get; set; }
    public int? ContextTokenCap { get; set; }
    public bool? ContextSummaryEnabled { get; set; }
    public bool? ContextIncludeBotHistory { get; set; }
    public string HandoffPausePolicy { get; set; } = "Group";
    public int ConfigurationVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
