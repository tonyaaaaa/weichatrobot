namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class FixedReplyTemplateGroupRuleEntity
{
    public Guid TemplateId { get; set; }
    public Guid GroupProfileId { get; set; }
    public string Effect { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
