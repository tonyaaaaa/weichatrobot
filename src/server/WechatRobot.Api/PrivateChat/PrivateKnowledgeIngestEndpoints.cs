using WechatRobot.Application.PrivateChat;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Api.PrivateChat;

public static class PrivateKnowledgeIngestEndpoints
{
    public static IEndpointRouteBuilder MapPrivateKnowledgeIngestEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/private-knowledge-ingests")
            .RequireAuthorization(SystemRoles.Admin);
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/retry", RetryAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        PrivateKnowledgeIngestOperationsService service,
        string? status,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default) =>
        Results.Ok(await service.ListAsync(status, skip, take, cancellationToken));

    private static async Task<IResult> GetAsync(
        Guid id,
        PrivateKnowledgeIngestOperationsService service,
        CancellationToken cancellationToken)
    {
        var batch = await service.GetAsync(id, cancellationToken);
        return batch is null ? Results.NotFound() : Results.Ok(batch);
    }

    private static async Task<IResult> RetryAsync(
        Guid id,
        RetryRequest request,
        PrivateKnowledgeIngestOperationsService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.RetryAsync(
                id,
                request.ExpectedVersion,
                cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (PrivateKnowledgeIngestConcurrencyException)
        {
            return Results.Conflict(new
            {
                code = "private_knowledge_ingest_concurrency_conflict"
            });
        }
        catch (PrivateKnowledgeIngestRetryException exception)
        {
            return Results.Conflict(new { code = exception.Code });
        }
    }

    private sealed record RetryRequest(int ExpectedVersion);
}
