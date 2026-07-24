namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class GroupRuleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupProfileId { get; set; }
    public int RuleKind { get; set; }
    public string IncludePattern { get; set; } = string.Empty;
    public int IncludePatternKind { get; set; }
    public string? ExcludePattern { get; set; }
    public int ExcludePatternKind { get; set; }
    public bool IgnoreCase { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
