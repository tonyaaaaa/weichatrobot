using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Api.Robots;

public static class RobotSettingsEndpoints
{
    public static IEndpointRouteBuilder MapRobotSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/robots").RequireAuthorization(SystemRoles.Admin);
        group.MapGet("/", ListAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(WechatRobotDbContext db, CancellationToken token)
    {
        var items = await db.RobotConfigs.AsNoTracking().OrderBy(item => item.Name)
            .Select(item => new RobotSettingsResponse(item.Id, item.Name, item.IsEnabled, item.SendRateLimitPerMinute, item.UpdatedAtUtc))
            .ToArrayAsync(token);
        return TypedResults.Ok(items);
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateRobotSettingsRequest request, WechatRobotDbContext db, CancellationToken token)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || request.SendRateLimitPerMinute is < 1 or > 60)
            return TypedResults.BadRequest(new { error = "Robot settings are invalid." });
        var robot = await db.RobotConfigs.SingleOrDefaultAsync(item => item.Id == id, token);
        if (robot is null) return TypedResults.NotFound();
        if (!robot.IsEnabled && request.IsEnabled)
            return Results.Conflict(new
            {
                error = "robot-probe-required",
                message = "Use the WorkTool administration endpoint and a successful connection test to enable this robot."
            });
        robot.Name = name;
        robot.IsEnabled = request.IsEnabled;
        robot.SendRateLimitPerMinute = request.SendRateLimitPerMinute;
        robot.SendRateTokens = Math.Min(robot.SendRateTokens, request.SendRateLimitPerMinute);
        robot.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        return TypedResults.Ok(new RobotSettingsResponse(robot.Id, robot.Name, robot.IsEnabled, robot.SendRateLimitPerMinute, robot.UpdatedAtUtc));
    }

    public sealed record UpdateRobotSettingsRequest(string Name, bool IsEnabled, int SendRateLimitPerMinute);
    public sealed record RobotSettingsResponse(Guid Id, string Name, bool IsEnabled, int SendRateLimitPerMinute, DateTime UpdatedAtUtc);
}
