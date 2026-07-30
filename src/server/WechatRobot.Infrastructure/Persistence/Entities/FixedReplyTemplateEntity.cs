namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class FixedReplyTemplateEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string IntentDescription { get; set; } = string.Empty;
    public string ReplyText { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool IsEnabled { get; set; }
    public int Version { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}
