namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class WorkerHeartbeatEntity
{
    public string Name { get; set; } = string.Empty;
    public DateTime LastSeenAtUtc { get; set; }
}
