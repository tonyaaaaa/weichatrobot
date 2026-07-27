using System.Security.Claims;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.Api.Knowledge;

public static class KnowledgeIndexEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeIndexEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var versions = endpoints.MapGroup("/api/knowledge/documents/{documentId:guid}/versions/{versionId:guid}")
            .RequireAuthorization(SystemRoles.KnowledgeOperator);
        versions.MapPost("/index", (Guid documentId, Guid versionId, IndexKnowledgeRequest request, QdrantKnowledgeService service, CancellationToken token) =>
            QueueAsync(documentId, versionId, request, false, service, token));
        versions.MapPost("/reindex", (Guid documentId, Guid versionId, IndexKnowledgeRequest request, QdrantKnowledgeService service, CancellationToken token) =>
            QueueAsync(documentId, versionId, request, true, service, token));

        var operations = endpoints.MapGroup("/api/knowledge").RequireAuthorization(SystemRoles.KnowledgeOperator);
        operations.MapPost("/index-jobs/{jobId:guid}/retry", RetryAsync);
        operations.MapPost("/documents/{documentId:guid}/disable", DisableAsync);
        operations.MapGet("/documents/{documentId:guid}/index-status", StatusAsync);
        return endpoints;
    }

    private static async Task<IResult> QueueAsync(Guid documentId, Guid versionId, IndexKnowledgeRequest request, bool reindex,
        QdrantKnowledgeService service, CancellationToken token)
    {
        try
        {
            var jobId = await service.QueueIndexAsync(documentId, versionId, request.TagIds ?? [], reindex, token);
            return Results.Accepted($"/api/knowledge/index-jobs/{jobId}", new { jobId, state = "pending", operation = reindex ? "reindex" : "index" });
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["tagIds"] = [exception.Message] }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = "index-state-conflict", message = exception.Message }); }
    }

    private static async Task<IResult> RetryAsync(Guid jobId, QdrantKnowledgeService service, CancellationToken token)
    {
        try { await service.RetryAsync(jobId, token); return Results.Accepted($"/api/knowledge/index-jobs/{jobId}", new { jobId, state = "pending" }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = "retry-state-conflict", message = exception.Message }); }
    }

    private static async Task<IResult> DisableAsync(
        Guid documentId,
        KnowledgeDocumentStateRequest request,
        ClaimsPrincipal principal,
        QdrantKnowledgeService service,
        CancellationToken token)
    {
        if (!TryGetActor(principal, out var actor)) return Results.Unauthorized();
        try
        {
            await service.DisableAsync(documentId, request.ExpectedStateVersion, actor, token);
            return Results.Accepted($"/api/knowledge/documents/{documentId}/index-status", new { documentId, state = "disabled" });
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (DocumentConcurrencyException exception) { return Results.Conflict(new { error = "document-concurrency-conflict", current = exception.Current }); }
        catch (DocumentDeleteRequestedException) { return Results.Conflict(new { error = "document-delete-requested" }); }
    }

    private static async Task<IResult> StatusAsync(Guid documentId, bool checkConsistency, QdrantKnowledgeService service, IVectorStore vectors, CancellationToken token)
    {
        try { return Results.Ok(await service.GetStatusAsync(documentId, vectors, checkConsistency, token)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (VectorStoreUnavailableException exception) { return Results.Json(new { error = "qdrant-unavailable", message = exception.Message }, statusCode: 503); }
    }

    private static bool TryGetActor(ClaimsPrincipal principal, out string actor)
    {
        actor = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.Identity?.Name
            ?? string.Empty;
        actor = actor.Trim();
        return actor.Length > 0;
    }
}

public sealed record IndexKnowledgeRequest(IReadOnlyList<Guid>? TagIds);
