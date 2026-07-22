namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class ConversationSessionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupProfileId { get; set; }
    public string SenderScopeKey { get; set; } = "*";
    public string? Summary { get; set; }
    public DateTime? ClearedAtUtc { get; set; }
    public DateTime LastActivityAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public int Version { get; set; }
    public long NextSequence { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
