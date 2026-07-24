using WechatRobot.Infrastructure.Health;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Api.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapWechatRobotHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }))
            .AllowAnonymous()
            .DisableRateLimiting();
        endpoints.MapGet("/api/admin/health/ready", ReadyAsync)
            .RequireAuthorization(SystemRoles.Admin)
            .RequireRateLimiting(Security.RateLimitPolicies.Ordinary);
        return endpoints;
    }

    private static async Task<IResult> ReadyAsync(
        IEnumerable<IComponentHealthProbe> probes,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var timeoutMilliseconds = configuration.GetValue("Health:ProbeTimeoutMilliseconds", 3000);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMilliseconds, 100, 10_000)));
        var components = await Task.WhenAll(probes.Select(probe =>
            CheckWithDeadlineAsync(probe, deadline.Token, cancellationToken)));

        var failedRequired = components.Any(value => value.Required && value.State == ComponentHealthState.Failed);
        var status = failedRequired
            ? "failed"
            : components.Any(value => value.State == ComponentHealthState.Failed) ? "degraded" : "healthy";
        var payload = new
        {
            status,
            checkedAtUtc = DateTime.UtcNow,
            components = components.Select(value => new
            {
                value.Name,
                status = value.State == ComponentHealthState.Healthy ? "healthy" : "failed",
                value.Required,
                value.Detail
            })
        };
        return failedRequired
            ? Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(payload);
    }

    private static async Task<ComponentHealthResult> CheckWithDeadlineAsync(
        IComponentHealthProbe probe,
        CancellationToken deadline,
        CancellationToken requestAborted)
    {
        try
        {
            return await probe.CheckAsync(deadline).WaitAsync(deadline);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            return new(probe.Name, ComponentHealthState.Failed, probe.Required, "timeout");
        }
    }
}
