namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class WorkToolRateLimitBucketEntity
{
    public string ScopeKey { get; set; } = string.Empty;
    public DateTime NextPermitAtUtc { get; set; }
    public int Version { get; set; }
}
