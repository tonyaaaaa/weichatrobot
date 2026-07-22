namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeChunkTagEntity
{
    public Guid KnowledgeChunkId { get; set; }
    public Guid KnowledgeTagId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
