namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class DeadLetterEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DurableJobId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
