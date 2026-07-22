using System.Security.Claims;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Api.Knowledge;

public static class KnowledgeReviewEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/knowledge/candidates").RequireAuthorization(SystemRoles.KnowledgeOperator);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", DetailAsync);
        group.MapPost("/{id:guid}/reviews", ReviewAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(string? status, int page, int pageSize, WechatRobotDbContext db, CancellationToken token)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);
        var query = db.KnowledgeCandidates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.HandoffCaseId, x.QuestionMessageId, x.Question, x.Status, x.KnowledgeDocumentVersionId,
                x.Version, x.CreatedAtUtc, x.UpdatedAtUtc, x.PublishedAtUtc }).ToArrayAsync(token);
        return TypedResults.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> DetailAsync(Guid id, WechatRobotDbContext db, CancellationToken token)
    {
        var item = await db.KnowledgeCandidates.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.HandoffCaseId,
            x.QuestionMessageId, x.Question, x.Answer, x.EvidenceJson, x.Status, x.KnowledgeDocumentVersionId, x.Version,
            x.CreatedAtUtc, x.UpdatedAtUtc, x.PublishedAtUtc }).SingleOrDefaultAsync(token);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item);
    }

    private static async Task<IResult> ReviewAsync(Guid id, KnowledgeReviewRequest request, ClaimsPrincipal user,
        KnowledgeCandidateService service, CancellationToken token)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var reviewer) || reviewer == Guid.Empty) return TypedResults.Unauthorized();
        try
        {
            return TypedResults.Ok(await service.ReviewAsync(new(id, reviewer, request.Decision, request.TagIds,
                request.RevisedAnswer, request.IdempotencyKey, request.ExpectedVersion), token));
        }
        catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        catch (HandoffConcurrencyException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
        catch (HandoffStateException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
        catch (ArgumentException exception) { return TypedResults.BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return TypedResults.Conflict(new { error = exception.Message }); }
    }
}

public sealed record KnowledgeReviewRequest(string Decision, IReadOnlyList<Guid>? TagIds, string? RevisedAnswer, string IdempotencyKey, int ExpectedVersion);
