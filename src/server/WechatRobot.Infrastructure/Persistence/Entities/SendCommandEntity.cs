namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class SendCommandEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RobotConfigId { get; set; }
    public Guid? GroupProfileId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
}
