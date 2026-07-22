using System.Security.Claims;
using System.Text.Json;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace WechatRobot.Api.Handoffs;

public static class HandoffEndpoints
{
    public static IEndpointRouteBuilder MapHandoffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/handoffs").RequireAuthorization(SystemRoles.HumanAgent);
        group.MapPost("/manual", StartManualAsync);
        group.MapPost("/{id:guid}/assign", AssignAsync);
        group.MapPost("/{id:guid}/resolve", ResolveAsync);
        group.MapPost("/{id:guid}/restore-ai", RestoreAsync);
        return endpoints;
    }

    private static async Task<IResult> StartManualAsync(ManualHandoffRequest request, ClaimsPrincipal user, HandoffService service,
        UserManager<ApplicationUser> users, CancellationToken token)
    {
        var actor = Actor(user);
        if (request.AssigneeUserId is { } assignee && !await IsHumanAgentAsync(users, assignee))
            return TypedResults.BadRequest(new { error = "Assignee must be an authenticated HumanAgent or Admin user." });
        try
        {
            var result = await service.StartAsync(new(request.QuestionMessageId, request.RobotConfigId, request.GroupProfileId,
                request.WorkToolRobotId, request.GroupName, "manual_transfer", JsonSerializer.Serialize(new { AuthenticatedActorUserId = actor, request.Reason }),
                request.PauseScope, request.StableSenderId, request.AssigneeUserId, request.AssigneeTarget,
                string.IsNullOrWhiteSpace(request.IdempotencyKey) ? $"manual-handoff:{request.QuestionMessageId:D}" : request.IdempotencyKey), token);
            return TypedResults.Ok(result);
        }
        catch (ArgumentException exception) { return TypedResults.BadRequest(new { error = exception.Message }); }
    }

    private static async Task<IResult> AssignAsync(Guid id, AssignHandoffRequest request, ClaimsPrincipal user, HandoffService service,
        UserManager<ApplicationUser> users, CancellationToken token)
    {
        if (!await IsHumanAgentAsync(users, request.AssigneeUserId))
            return TypedResults.BadRequest(new { error = "Assignee must be an authenticated HumanAgent or Admin user." });
        return await ExecuteAsync(() => service.AssignAsync(id, Actor(user), request.AssigneeUserId, request.ExpectedVersion, token));
    }
    private static async Task<IResult> ResolveAsync(Guid id, ResolveHandoffRequest request, ClaimsPrincipal user, HandoffService service, CancellationToken token) =>
        await ExecuteAsync(() => service.ResolveAsync(id, Actor(user), request.FinalAnswer, request.ExpectedVersion, token));
    private static async Task<IResult> RestoreAsync(Guid id, VersionedHandoffRequest request, ClaimsPrincipal user, HandoffService service, CancellationToken token) =>
        await ExecuteAsync(() => service.RestoreAiAsync(id, Actor(user), request.ExpectedVersion, token));

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try { return TypedResults.Ok(await action()); }
        catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        catch (HandoffConcurrencyException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
        catch (HandoffStateException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
        catch (ArgumentException exception) { return TypedResults.BadRequest(new { error = exception.Message }); }
    }

    private static Guid Actor(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
        ? id : throw new UnauthorizedAccessException("Authenticated user id is missing.");
    private static async Task<bool> IsHumanAgentAsync(UserManager<ApplicationUser> users, Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString("D"));
        return user is not null && (await users.IsInRoleAsync(user, SystemRoles.HumanAgent) || await users.IsInRoleAsync(user, SystemRoles.Admin));
    }
}

public sealed record ManualHandoffRequest(Guid QuestionMessageId, Guid RobotConfigId, Guid GroupProfileId, string WorkToolRobotId, string GroupName,
    string Reason, HandoffPauseScope PauseScope, string? StableSenderId, Guid? AssigneeUserId, string AssigneeTarget, string? IdempotencyKey);
public sealed record AssignHandoffRequest(Guid AssigneeUserId, int ExpectedVersion);
public sealed record ResolveHandoffRequest(string FinalAnswer, int ExpectedVersion);
public sealed record VersionedHandoffRequest(int ExpectedVersion);
