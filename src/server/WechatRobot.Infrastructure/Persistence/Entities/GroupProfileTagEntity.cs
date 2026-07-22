namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class GroupProfileTagEntity
{
    public Guid GroupProfileId { get; set; }
    public Guid KnowledgeTagId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
