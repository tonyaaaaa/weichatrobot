using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WechatRobot.Api.WorkTool;

public static class WorkToolCallbackRateLimitPolicy
{
    public const string Name = "worktool-callback";

    public static void Add(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!context.Request.Path.StartsWithSegments("/api/worktool/callback"))
                {
                    return RateLimitPartition.GetNoLimiter("non-worktool-callback");
                }

                var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return CreateLimiter($"worktool-callback-source:{source}");
            });
            options.AddPolicy(Name, context => CreateLimiter($"worktool-callback-robot:{context.Request.RouteValues["robotCode"]?.ToString() ?? "unknown"}"));
        });
    }

    private static RateLimitPartition<string> CreateLimiter(string partitionKey) => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 50,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
}
