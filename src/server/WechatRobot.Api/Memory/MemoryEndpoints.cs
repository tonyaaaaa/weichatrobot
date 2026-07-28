using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Memory;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Api.Memory;

public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/memory")
            .RequireAuthorization(SystemRoles.KnowledgeOperator);
        group.MapGet("/candidates", ListCandidatesAsync);
        group.MapPost("/candidates/{id:guid}/edit", EditCandidateAsync);
        group.MapPost("/candidates/{id:guid}/promote", PromoteCandidateAsync);
        group.MapPost("/candidates/{id:guid}/reject", RejectCandidateAsync);
        group.MapPost("/candidates/{id:guid}/reorganize", ReorganizeCandidateAsync);
        group.MapGet("/entries", ListEntriesAsync);
        group.MapPost("/entries/{id:guid}/forget", ForgetEntryAsync);
        group.MapPost("/entries/{id:guid}/restore", RestoreEntryAsync);
        group.MapGet("/jobs", ListJobsAsync);
        group.MapPost("/jobs/{id:guid}/retry", RetryJobAsync);
        return endpoints;
    }

    private static async Task<IResult> ListCandidatesAsync(
        Guid? groupProfileId,
        string? scopeType,
        string? memoryType,
        string? status,
        int? page,
        int? pageSize,
        WechatRobotDbContext database,
        CancellationToken cancellationToken)
    {
        if (!TryPage(page, pageSize, out var actualPage, out var actualPageSize))
            return Results.BadRequest(new { error = "Invalid pagination." });
        var query = database.MemoryCandidates.AsNoTracking()
            .Where(x => groupProfileId == null || x.GroupProfileId == groupProfileId)
            .Where(x => string.IsNullOrEmpty(scopeType) || x.ScopeType == scopeType)
            .Where(x => string.IsNullOrEmpty(memoryType) || x.MemoryType == memoryType)
            .Where(x => string.IsNullOrEmpty(status) || x.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((actualPage - 1) * actualPageSize).Take(actualPageSize)
            .Select(x => new
            {
                x.Id, x.ScopeType, x.RobotConfigId, x.GroupProfileId, x.SubjectKey,
                x.SubjectDisplayName, x.MemoryType, x.Content, x.Confidence, x.IsExplicit,
                x.ObservationCount, x.DistinctSessionCount, x.DistinctDayCount,
                x.HasUnresolvedConflict, x.Status, x.PromotedMemoryEntryId,
                x.KnowledgeCandidateId, x.Version, x.CreatedAtUtc, x.UpdatedAtUtc
            }).ToArrayAsync(cancellationToken);
        return Results.Ok(new { items, total, page = actualPage, pageSize = actualPageSize });
    }

    private static async Task<IResult> ListEntriesAsync(
        Guid? groupProfileId,
        string? scopeType,
        string? memoryType,
        string? status,
        int? page,
        int? pageSize,
        WechatRobotDbContext database,
        CancellationToken cancellationToken)
    {
        if (!TryPage(page, pageSize, out var actualPage, out var actualPageSize))
            return Results.BadRequest(new { error = "Invalid pagination." });
        var query = database.MemoryEntries.AsNoTracking()
            .Where(x => groupProfileId == null || x.GroupProfileId == groupProfileId)
            .Where(x => string.IsNullOrEmpty(scopeType) || x.ScopeType == scopeType)
            .Where(x => string.IsNullOrEmpty(memoryType) || x.MemoryType == memoryType)
            .Where(x => string.IsNullOrEmpty(status) || x.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((actualPage - 1) * actualPageSize).Take(actualPageSize)
            .Select(x => new
            {
                x.Id, x.ScopeType, x.RobotConfigId, x.GroupProfileId, x.SubjectKey,
                x.SubjectDisplayName, x.MemoryType, x.Content, x.Confidence, x.Status,
                x.SupersedesMemoryEntryId, x.SourceCandidateId, x.ValidFromUtc, x.ExpiresAtUtc,
                x.RecallCount, x.LastRecalledAtUtc, x.StatusVersion, x.Version,
                x.CreatedAtUtc, x.UpdatedAtUtc
            }).ToArrayAsync(cancellationToken);
        return Results.Ok(new { items, total, page = actualPage, pageSize = actualPageSize });
    }

    private static async Task<IResult> ListJobsAsync(
        Guid? groupProfileId,
        string? status,
        int? page,
        int? pageSize,
        WechatRobotDbContext database,
        CancellationToken cancellationToken)
    {
        if (!TryPage(page, pageSize, out var actualPage, out var actualPageSize))
            return Results.BadRequest(new { error = "Invalid pagination." });
        var types = new[] { "ExtractConversationMemory", "MaintainLongTermMemory", "IndexMemoryEntry", "RemoveMemoryEntryFromIndex" };
        var query = database.DurableJobs.AsNoTracking()
            .Where(x => types.Contains(x.JobType))
            .Where(x => groupProfileId == null || x.GroupProfileId == groupProfileId)
            .Where(x => string.IsNullOrEmpty(status) || x.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((actualPage - 1) * actualPageSize).Take(actualPageSize)
            .Select(x => new
            {
                x.Id, x.JobType, x.GroupProfileId, x.Status, x.AttemptCount,
                x.AvailableAtUtc, x.NextAttemptAtUtc, x.CompletedAtUtc,
                x.Version, x.CreatedAtUtc, x.UpdatedAtUtc
            }).ToArrayAsync(cancellationToken);
        return Results.Ok(new { items, total, page = actualPage, pageSize = actualPageSize });
    }

    private static Task<IResult> EditCandidateAsync(
        Guid id, EditCandidateRequest request, ClaimsPrincipal principal,
        MemoryAdministrationService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.UpdateCandidateAsync(
            id, request.Content, request.Confidence, request.ExpectedVersion, Actor(principal), cancellationToken)));

    private static Task<IResult> PromoteCandidateAsync(
        Guid id, VersionRequest request, ClaimsPrincipal principal,
        MemoryAdministrationService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.PromoteCandidateAsync(
            id, request.ExpectedVersion, Actor(principal), cancellationToken)));

    private static Task<IResult> RejectCandidateAsync(
        Guid id, VersionRequest request, ClaimsPrincipal principal,
        MemoryAdministrationService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.RejectCandidateAsync(
            id, request.ExpectedVersion, Actor(principal), cancellationToken)));

    private static Task<IResult> ReorganizeCandidateAsync(
        Guid id, VersionRequest request, ClaimsPrincipal principal,
        MemoryAdministrationService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await service.ReorganizeCandidateAsync(
                id, request.ExpectedVersion, Actor(principal), cancellationToken);
            return Results.NoContent();
        });

    private static Task<IResult> ForgetEntryAsync(
        Guid id, VersionRequest request, ClaimsPrincipal principal,
        MemoryAdministrationService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.ChangeEntryStatusAsync(
            id, "forgotten", request.ExpectedVersion, Actor(principal), cancellationToken)));

    private static Task<IResult> RestoreEntryAsync(
        Guid id, VersionRequest request, ClaimsPrincipal principal,
        MemoryAdministrationService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.ChangeEntryStatusAsync(
            id, "active", request.ExpectedVersion, Actor(principal), cancellationToken)));

    private static Task<IResult> RetryJobAsync(
        Guid id, VersionRequest request, ClaimsPrincipal principal,
        MemoryAdministrationService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await service.RetryJobAsync(id, request.ExpectedVersion, Actor(principal), cancellationToken);
            return Results.NoContent();
        });

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (MemoryConcurrencyException) { return Results.Conflict(new { error = "The memory record changed. Refresh and retry." }); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }

    private static bool TryPage(int? page, int? pageSize, out int actualPage, out int actualPageSize)
    {
        actualPage = page ?? 1;
        actualPageSize = pageSize ?? 20;
        return actualPage is >= 1 and <= 1_000_000 && actualPageSize is >= 1 and <= 100;
    }

    private static string Actor(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

public sealed record VersionRequest(int ExpectedVersion);
public sealed record EditCandidateRequest(string Content, double Confidence, int ExpectedVersion);
