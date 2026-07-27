namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolRateLimitOptions
{
    public const string SectionName = "WorkTool:RateLimit";

    public string ScopeKey { get; set; } = "default-egress";
    public int RequestsPerMinute { get; set; } = 60;
    public int MaxWaitSeconds { get; set; } = 15;
}
