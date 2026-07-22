using System.Security.Claims;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Api.Knowledge;

public static class KnowledgeReviewEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/knowledge/candidates/{id:guid}/reviews", ReviewAsync)
            .RequireAuthorization(SystemRoles.KnowledgeOperator);
        return endpoints;
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
    }
}

public sealed record KnowledgeReviewRequest(string Decision, IReadOnlyList<Guid> TagIds, string? RevisedAnswer, string IdempotencyKey, int ExpectedVersion);
