using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
        endpoints.MapPost("/api/worktool/config-callback/{robotCode}", HandleConfigurationCallbackAsync)
            .AllowAnonymous()
            .RequireRateLimiting(WorkToolCallbackRateLimitPolicy.Name);
        endpoints.MapPost("/api/worktool/command-results/{robotCode}", HandleCommandResultAsync)
            .AllowAnonymous()
            .RequireRateLimiting(WorkToolCallbackRateLimitPolicy.Name);
        return endpoints;
    }

    private static async Task<IResult> HandleCommandResultAsync(
        string robotCode,
        string? token,
        WorkToolCommandResultDto result,
        WechatRobotDbContext database,
        WorkToolCommandResultProcessor processor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("WorkToolCommandResultCallback");
        if (!WorkToolCallbackDto.IsIdentifierWithinLimit(robotCode))
            return Results.BadRequest();

        var robot = await database.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(
            config => config.CallbackRouteCode == robotCode && config.IsEnabled,
            cancellationToken);
        if (robot is null || !WorkToolCallbackSecretVerifier.Matches(
                token,
                robot.CallbackSecretHash,
                robot.PreviousCallbackSecretHash,
                robot.PreviousCallbackSecretExpiresAtUtc,
                DateTime.UtcNow))
        {
            logger.LogWarning("WorkTool command-result callback rejected: authentication failed.");
            return Results.Unauthorized();
        }

        if (!result.IsValid(out var reason))
        {
            logger.LogWarning("WorkTool command-result callback rejected: {Reason}.", reason);
            return Results.BadRequest();
        }

        try
        {
            await processor.ProcessAsync(robot.Id, result, cancellationToken);
            return Results.Json(new WorkToolCallbackAcceptedResponse());
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("WorkTool command-result callback persistence failed.");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleConfigurationCallbackAsync(
        string robotCode,
        JsonElement payload,
        WechatRobotDbContext database,
        CancellationToken cancellationToken)
    {
        if (!WorkToolCallbackDto.IsIdentifierWithinLimit(robotCode) ||
            payload.ValueKind is not JsonValueKind.Object)
            return Results.BadRequest();
        var exists = await database.RobotConfigs.AsNoTracking()
            .AnyAsync(robot => robot.CallbackRouteCode == robotCode && robot.IsEnabled, cancellationToken);
        return exists
            ? Results.Json(new WorkToolCallbackAcceptedResponse())
            : Results.Unauthorized();
    }

    private static async Task<IResult> HandleAsync(
        string robotCode,
        string? token,
        WorkToolCallbackDto callback,
        WechatRobotDbContext database,
        InboundMessageService inboundMessages,
        IOptions<WorkToolCallbackOptions> callbackOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("WorkToolCallback");
        if (!WorkToolCallbackDto.IsIdentifierWithinLimit(robotCode))
        {
            logger.LogWarning("WorkTool callback rejected: invalid callback shape.");
            return Results.BadRequest();
        }

        using var ingestionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ingestionDeadline.CancelAfter(callbackOptions.Value.IngestionDeadline);
        var ingestionToken = ingestionDeadline.Token;

        try
        {
            var robot = await database.RobotConfigs.SingleOrDefaultAsync(config => config.CallbackRouteCode == robotCode && config.IsEnabled, ingestionToken);
            if (robot is null || !WorkToolCallbackSecretVerifier.Matches(
                    token,
                    robot.CallbackSecretHash,
                    robot.PreviousCallbackSecretHash,
                    robot.PreviousCallbackSecretExpiresAtUtc,
                    DateTime.UtcNow))
            {
                logger.LogWarning("WorkTool callback rejected: authentication failed.");
                return Results.Unauthorized();
            }

            var classification = callback.Classify();
            if (classification.Disposition == WorkToolCallbackDisposition.Reject)
            {
                logger.LogWarning(
                    "WorkTool callback rejected: {Reason}.",
                    classification.Reason);
                return Results.BadRequest();
            }
            if (classification.Disposition == WorkToolCallbackDisposition.Ignore)
            {
                logger.LogInformation(
                    "WorkTool callback ignored with reason {Reason}, room type {RoomType}, text type {TextType}.",
                    classification.Reason,
                    callback.RoomType,
                    callback.TextType);
                return Results.Json(new WorkToolCallbackAcceptedResponse("ignored"));
            }

            await inboundMessages.IngestAsync(robot.Id, robotCode, callback, ingestionToken);
        }
        catch (OperationCanceledException) when (ingestionDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError("WorkTool callback persistence timed out.");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        catch (Exception)
        {
            logger.LogError("WorkTool callback persistence failed.");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Results.Json(new WorkToolCallbackAcceptedResponse());
    }

    private sealed class WorkToolCallbackAcceptedResponse(string message = "accepted")
    {
        public int Code => 0;
        public string Message => message;
    }
}
