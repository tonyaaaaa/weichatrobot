using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WechatRobot.Api.WorkTool;

public static class WorkToolCallbackRateLimitPolicy
{
    public const string Name = "worktool-callback";

    public static void Add(IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(Name, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Request.RouteValues["robotCode"]?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 50,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                })));
    }
}
