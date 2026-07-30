namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class KnowledgeTagEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool IsGlobalPublic { get; set; }
    public string? SystemKind { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
