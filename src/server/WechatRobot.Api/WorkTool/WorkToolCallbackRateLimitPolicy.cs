namespace WechatRobot.Api.WorkTool;

public static class WorkToolCallbackRateLimitPolicy
{
    public const string Name = Security.RateLimitPolicies.Callback;

    public static void Add(IServiceCollection services) => Security.RateLimitPolicies.AddApiRateLimits(services);
}
