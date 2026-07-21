using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Api.WorkTool;

public static class WorkToolCallbackEndpoints
{
    public static IEndpointRouteBuilder MapWorkToolCallbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/worktool/callback/{robotCode}", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(WorkToolCallbackRateLimitPolicy.Name);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string robotCode,
        string? token,
        WorkToolCallbackDto callback,
        WechatRobotDbContext database,
        InboundMessageService inboundMessages,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("WorkToolCallback");
        var robot = await database.RobotConfigs.SingleOrDefaultAsync(config => config.WorkToolRobotId == robotCode && config.IsEnabled, cancellationToken);
        if (robot is null || !SecretMatches(token, robot.CallbackSecretHash))
        {
            logger.LogWarning("WorkTool callback rejected: authentication failed.");
            return Results.Unauthorized();
        }

        if (!callback.IsSupportedGroupText(out var reason))
        {
            logger.LogWarning("WorkTool callback rejected: {Reason}.", reason);
            return Results.BadRequest();
        }

        try
        {
            await inboundMessages.IngestAsync(robot.Id, robot.WorkToolRobotId, callback, cancellationToken);
        }
        catch (Exception)
        {
            logger.LogError("WorkTool callback persistence failed.");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Results.Json(new WorkToolCallbackAcceptedResponse());
    }

    private static bool SecretMatches(string? submittedSecret, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(submittedSecret) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        try
        {
            var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(submittedSecret));
            var configuredHash = Convert.FromHexString(storedHash);
            return configuredHash.Length == submittedHash.Length && CryptographicOperations.FixedTimeEquals(submittedHash, configuredHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class WorkToolCallbackAcceptedResponse
    {
        public int Code => 0;
        public string Message => "accepted";
    }
}
