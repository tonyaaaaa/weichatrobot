using System.Security.Claims;
using System.Text.Json;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Api.Handoffs;

public static class HandoffEndpoints
{
    public static IEndpointRouteBuilder MapHandoffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/handoffs").RequireAuthorization(SystemRoles.HumanAgent);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", DetailAsync);
        group.MapGet("/{id:guid}/messages", MessagesAsync);
        group.MapGet("/{id:guid}/transitions", TransitionsAsync);
        group.MapPost("/manual", StartManualAsync);
        group.MapPost("/{id:guid}/assign", AssignAsync);
        group.MapPost("/{id:guid}/resolve", ResolveAsync);
        group.MapPost("/{id:guid}/restore-ai", RestoreAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(string? state, int page, int pageSize, WechatRobotDbContext db, CancellationToken token)
    {
        if (!Pagination.TryNormalize(page, pageSize, out page, out pageSize, out var skip)) return InvalidPage();
        var query = db.HandoffCases.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(state)) query = query.Where(x => x.State == state);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).Skip(skip).Take(pageSize)
            .Select(x => new { x.Id, x.QuestionMessageId, x.GroupProfileId, x.State, x.ReasonCode, x.PauseScope, x.StableSenderId,
                x.AssigneeUserId, x.ResolvedByUserId, x.Version, x.CreatedAtUtc, x.UpdatedAtUtc }).ToArrayAsync(token);
        return TypedResults.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> DetailAsync(Guid id, WechatRobotDbContext db, CancellationToken token)
    {
        var item = await db.HandoffCases.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.QuestionMessageId,
            x.RobotConfigId, x.GroupProfileId, x.State, x.ReasonCode, x.EvidenceJson, x.PauseScope, x.StableSenderId,
            x.AssigneeUserId, x.ResolvedByUserId, x.FinalAnswer, x.Version, x.CreatedAtUtc, x.UpdatedAtUtc }).SingleOrDefaultAsync(token);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item);
    }

    private static async Task<IResult> MessagesAsync(Guid id, int page, int pageSize, WechatRobotDbContext db, CancellationToken token)
    {
        if (!Pagination.TryNormalize(page, pageSize, out page, out pageSize, out var skip)) return InvalidPage();
        if (!await db.HandoffCases.AnyAsync(x => x.Id == id, token)) return TypedResults.NotFound();
        var query = db.HandoffMessages.AsNoTracking().Where(x => x.HandoffCaseId == id);
        var total = await query.CountAsync(token);
        var items = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(skip).Take(pageSize)
            .Select(x => new { x.Id, x.ExternalMessageId, x.SenderDisplayName, x.AuthenticatedUserId, x.AuthenticationKind, x.Text, x.CreatedAtUtc }).ToArrayAsync(token);
        return TypedResults.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> TransitionsAsync(Guid id, int page, int pageSize, WechatRobotDbContext db, CancellationToken token)
    {
        if (!Pagination.TryNormalize(page, pageSize, out page, out pageSize, out var skip)) return InvalidPage();
        if (!await db.HandoffCases.AnyAsync(x => x.Id == id, token)) return TypedResults.NotFound();
        var query = db.HandoffTransitions.AsNoTracking().Where(x => x.HandoffCaseId == id);
        var total = await query.CountAsync(token);
        var items = await query.OrderBy(x => x.Sequence).ThenBy(x => x.Id).Skip(skip).Take(pageSize)
            .Select(x => new { x.Id, x.ActorUserId, x.Sequence, x.FromState, x.ToState, x.ReasonCode, x.CreatedAtUtc }).ToArrayAsync(token);
        return TypedResults.Ok(new { items, total, page, pageSize });
    }

    private static IResult InvalidPage() => TypedResults.BadRequest(new { error = "Page must not exceed 1000000." });

    private static async Task<IResult> StartManualAsync(ManualHandoffRequest request, ClaimsPrincipal user, HandoffService service,
        UserManager<ApplicationUser> users, CancellationToken token)
    {
        if (!TryActor(user, out var actor)) return TypedResults.Unauthorized();
        if (request.AssigneeUserId is { } assignee && !await IsHumanAgentAsync(users, assignee))
            return TypedResults.BadRequest(new { error = "Assignee must be an authenticated HumanAgent or Admin user." });
        try
        {
            var result = await service.StartManualAsync(new(request.QuestionMessageId, request.Reason, request.PauseScope,
                request.AssigneeUserId, request.IdempotencyKey, actor), token);
            return TypedResults.Ok(result);
        }
        catch (ArgumentException exception) { return TypedResults.BadRequest(new { error = exception.Message }); }
        catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        catch (HandoffStateException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
        catch (HandoffConcurrencyException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
    }

    private static async Task<IResult> AssignAsync(Guid id, AssignHandoffRequest request, ClaimsPrincipal user, HandoffService service,
        UserManager<ApplicationUser> users, CancellationToken token)
    {
        if (!TryActor(user, out var actor)) return TypedResults.Unauthorized();
        if (!await IsHumanAgentAsync(users, request.AssigneeUserId))
            return TypedResults.BadRequest(new { error = "Assignee must be an authenticated HumanAgent or Admin user." });
        return await ExecuteAsync(() => service.AssignAsync(id, actor, request.AssigneeUserId, request.ExpectedVersion, token));
    }
    private static async Task<IResult> ResolveAsync(Guid id, ResolveHandoffRequest request, ClaimsPrincipal user, HandoffService service, CancellationToken token) =>
        TryActor(user, out var actor) ? await ExecuteAsync(() => service.ResolveAsync(id, actor, request.FinalAnswer, request.ExpectedVersion, token)) : TypedResults.Unauthorized();
    private static async Task<IResult> RestoreAsync(Guid id, VersionedHandoffRequest request, ClaimsPrincipal user, HandoffService service, CancellationToken token) =>
        TryActor(user, out var actor) ? await ExecuteAsync(() => service.RestoreAiAsync(id, actor, request.ExpectedVersion, token)) : TypedResults.Unauthorized();

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try { return TypedResults.Ok(await action()); }
        catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        catch (HandoffConcurrencyException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
        catch (HandoffStateException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
        catch (ArgumentException exception) { return TypedResults.BadRequest(new { error = exception.Message }); }
    }

    private static bool TryActor(ClaimsPrincipal user, out Guid id) => Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out id) && id != Guid.Empty;
    private static async Task<bool> IsHumanAgentAsync(UserManager<ApplicationUser> users, Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString("D"));
        return user is not null && (await users.IsInRoleAsync(user, SystemRoles.HumanAgent) || await users.IsInRoleAsync(user, SystemRoles.Admin));
    }
}

public sealed record ManualHandoffRequest(Guid QuestionMessageId, string Reason, HandoffPauseScope PauseScope, Guid? AssigneeUserId, string IdempotencyKey);
public sealed record AssignHandoffRequest(Guid AssigneeUserId, int ExpectedVersion);
public sealed record ResolveHandoffRequest(string FinalAnswer, int ExpectedVersion);
public sealed record VersionedHandoffRequest(int ExpectedVersion);
