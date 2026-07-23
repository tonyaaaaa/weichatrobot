using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WechatRobot.Api.Security;

public static class RateLimitPolicies
{
    public const string Login = "login";
    public const string Callback = "worktool-callback";
    public const string Upload = "document-upload";
    public const string WorkToolCommands = "worktool-commands";
    public const string Ordinary = "ordinary-api";

    public static IServiceCollection AddApiRateLimits(this IServiceCollection services, bool disableEnforcement = false)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            if (disableEnforcement)
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    context => context.Request.Path.StartsWithSegments("/api/worktool/callback")
                        ? Fixed($"testing-callback-source:{Partition(context)}", 50)
                        : RateLimitPartition.GetNoLimiter("testing"));
                options.AddPolicy(Login, _ => RateLimitPartition.GetNoLimiter("testing-login"));
                options.AddPolicy(Callback, context => Fixed(
                    $"testing-callback-robot:{context.Request.RouteValues["robotCode"] ?? "unknown"}", 50));
                options.AddPolicy(Upload, _ => RateLimitPartition.GetNoLimiter("testing-upload"));
                options.AddPolicy(WorkToolCommands, _ => RateLimitPartition.GetNoLimiter("testing-worktool"));
                options.AddPolicy(Ordinary, _ => RateLimitPartition.GetNoLimiter("testing-ordinary"));
                return;
            }
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.Request.Path.StartsWithSegments("/health/live"))
                {
                    return RateLimitPartition.GetNoLimiter("liveness");
                }

                var (policy, permits) = Classify(context.Request.Path);
                return Fixed($"{policy}:{Partition(context)}", permits);
            });
            options.AddPolicy(Login, context => Fixed($"login:{Partition(context)}", 5));
            options.AddPolicy(Callback, context => Fixed(
                $"callback:{context.Request.RouteValues["robotCode"] ?? "unknown"}:{Partition(context)}", 50));
            options.AddPolicy(Upload, context => Fixed($"upload:{Partition(context)}", 10));
            options.AddPolicy(WorkToolCommands, context => Fixed($"worktool:{Partition(context)}", 10));
            options.AddPolicy(Ordinary, context => Fixed($"ordinary:{Partition(context)}", 120));
        });
        return services;
    }

    private static (string Policy, int Permits) Classify(PathString path) =>
        path.StartsWithSegments("/api/auth/login") ? (Login, 5) :
        path.StartsWithSegments("/api/worktool/callback") ? (Callback, 50) :
        path.StartsWithSegments("/api/knowledge/documents") ? (Upload, 10) :
        path.StartsWithSegments("/api/admin/worktool") ? (WorkToolCommands, 10) :
        (Ordinary, 120);

    private static string Partition(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";

    private static RateLimitPartition<string> Fixed(string key, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
}
