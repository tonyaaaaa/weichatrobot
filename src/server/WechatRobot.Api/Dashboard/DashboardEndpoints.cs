using WechatRobot.Api.Security;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Api.Dashboard;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/admin/dashboard/summary",
                async (
                    DashboardSummaryService service,
                    CancellationToken cancellationToken) =>
                    TypedResults.Ok(await service.GetAsync(cancellationToken)))
            .RequireAuthorization(SystemRoles.Admin)
            .RequireRateLimiting(RateLimitPolicies.Ordinary);
        return endpoints;
    }
}
