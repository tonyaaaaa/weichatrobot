using System.Security.Claims;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.Api.Knowledge;

public static class KnowledgeTagEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeTagEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/knowledge/tags")
            .RequireAuthorization(SystemRoles.KnowledgeOperator);
        group.MapGet("", ListAsync);
        group.MapGet("/options", OptionsAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapPatch("/{id:guid}/enabled", SetEnabledAsync);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(SystemRoles.Admin);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        KnowledgeTagManager manager,
        string? query,
        bool? isEnabled,
        bool? isGlobalPublic,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Results.Ok(await manager.ListAsync(
            query,
            isEnabled,
            isGlobalPublic,
            page,
            pageSize,
            cancellationToken));

    private static async Task<IResult> OptionsAsync(
        KnowledgeTagManager manager,
        CancellationToken cancellationToken) =>
        Results.Ok(await manager.ListOptionsAsync(cancellationToken));

    private static async Task<IResult> CreateAsync(
        KnowledgeTagDraft request,
        ClaimsPrincipal principal,
        KnowledgeTagManager manager,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var actor))
        {
            return Results.Unauthorized();
        }

        return ToResult(await manager.CreateAsync(actor, request, cancellationToken));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        KnowledgeTagUpdate request,
        ClaimsPrincipal principal,
        KnowledgeTagManager manager,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var actor))
        {
            return Results.Unauthorized();
        }

        return ToResult(await manager.UpdateAsync(id, actor, request, cancellationToken));
    }

    private static async Task<IResult> SetEnabledAsync(
        Guid id,
        KnowledgeTagStateUpdate request,
        ClaimsPrincipal principal,
        KnowledgeTagManager manager,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var actor))
        {
            return Results.Unauthorized();
        }

        return ToResult(await manager.SetEnabledAsync(id, actor, request, cancellationToken));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        int expectedVersion,
        ClaimsPrincipal principal,
        KnowledgeTagManager manager,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var actor))
        {
            return Results.Unauthorized();
        }

        return ToResult(await manager.DeleteAsync(
            id,
            actor,
            expectedVersion,
            cancellationToken));
    }

    private static bool TryGetActor(ClaimsPrincipal principal, out string actor)
    {
        actor = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.Identity?.Name
            ?? string.Empty;
        actor = actor.Trim();
        return actor.Length > 0;
    }

    private static IResult ToResult(KnowledgeTagMutationResult result) =>
        result.Status switch
        {
            KnowledgeTagMutationStatus.Succeeded => Results.Ok(result.Tag),
            KnowledgeTagMutationStatus.InvalidInput => Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["name"] = [result.Error ?? "knowledge-tag-name-invalid"]
                }),
            KnowledgeTagMutationStatus.NotFound => Results.NotFound(),
            KnowledgeTagMutationStatus.NameConflict => Results.Conflict(new
            {
                error = "knowledge-tag-name-conflict",
                current = result.Tag
            }),
            KnowledgeTagMutationStatus.ConcurrencyConflict => Results.Conflict(new
            {
                error = "knowledge-tag-concurrency-conflict",
                current = result.Tag
            }),
            KnowledgeTagMutationStatus.Referenced => Results.Conflict(new
            {
                error = "knowledge-tag-referenced",
                current = result.Tag,
                references = result.References
            }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
}
