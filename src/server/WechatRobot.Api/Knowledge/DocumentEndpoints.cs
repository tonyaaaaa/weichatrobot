using System.Security.Claims;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Api.Security;

namespace WechatRobot.Api.Knowledge;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var documents = endpoints.MapGroup("/api/knowledge/documents").RequireAuthorization(SystemRoles.KnowledgeOperator);
        documents.MapGet("", ListAsync);
        documents.MapGet("/{documentId:guid}", DetailAsync);
        documents.MapGet("/{documentId:guid}/versions", VersionsAsync);
        documents.MapPost("", UploadAsync).DisableAntiforgery().RequireRateLimiting(RateLimitPolicies.Upload);
        documents.MapPost("/{documentId:guid}/retry-upload", RetryAsync);
        documents.MapDelete("/{documentId:guid}/physical", RequestPhysicalDeleteAsync).RequireAuthorization(SystemRoles.Admin);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        KnowledgeDocumentAdministrationQuery queryService,
        string? query,
        string? status,
        string? sourceKind,
        Guid? tagId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Results.Ok(await queryService.ListAsync(
            query,
            status,
            sourceKind,
            tagId,
            page,
            pageSize,
            cancellationToken));

    private static async Task<IResult> DetailAsync(
        Guid documentId,
        KnowledgeDocumentAdministrationQuery queryService,
        CancellationToken cancellationToken)
    {
        var detail = await queryService.GetAsync(documentId, cancellationToken);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> VersionsAsync(
        Guid documentId,
        KnowledgeDocumentAdministrationQuery queryService,
        CancellationToken cancellationToken)
    {
        var detail = await queryService.GetAsync(documentId, cancellationToken);
        return detail is null ? Results.NotFound() : Results.Ok(detail.Versions);
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, DocumentUploadService service, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType) return MissingFile();
        IFormCollection form;
        try { form = await request.ReadFormAsync(cancellationToken); }
        catch (InvalidDataException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        var file = form.Files.GetFile("file");
        if (file is null) return MissingFile();
        var documentId = Guid.TryParse(form["documentId"], out var parsed) ? parsed : (Guid?)null;
        try
        {
            await using var stream = file.OpenReadStream();
            var result = await service.UploadAsync(documentId, file.FileName, file.ContentType, stream, cancellationToken);
            return result.ProviderSucceeded
                ? Results.Created($"/api/knowledge/documents/{result.DocumentId}", ToResponse(result))
                : Results.Json(ToResponse(result), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (DocumentUploadValidationException exception) { return Results.ValidationProblem(Problem("file", exception.Message)); }
        catch (DuplicateDocumentContentException) { return Duplicate(); }
        catch (DocumentDeletedException) { return Results.Conflict(new { error = "document-deleted" }); }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "document-not-writable" }); }
    }

    private static async Task<IResult> RetryAsync(
        Guid documentId,
        KnowledgeDocumentStateRequest request,
        ClaimsPrincipal principal,
        DocumentUploadService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var actor)) return Results.Unauthorized();
        try
        {
            var result = await service.RetryAsync(
                documentId,
                request.ExpectedStateVersion,
                actor,
                cancellationToken);
            return result.ProviderSucceeded ? Results.Ok(ToResponse(result)) : Results.Json(ToResponse(result), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (DocumentNotFoundException) { return Results.NotFound(); }
        catch (DocumentConcurrencyException exception) { return ConcurrencyConflict(exception); }
        catch (DocumentDeleteRequestedException) { return Results.Conflict(new { error = "document-delete-requested" }); }
        catch (DocumentDeletedException) { return Results.Conflict(new { error = "document-state-conflict" }); }
        catch (DocumentNotRetryableException) { return Results.Conflict(new { error = "document-not-retryable" }); }
    }

    private static async Task<IResult> RequestPhysicalDeleteAsync(
        Guid documentId,
        int expectedStateVersion,
        ClaimsPrincipal principal,
        DocumentUploadService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(principal, out var actor)) return Results.Unauthorized();
        try
        {
            await service.RequestPhysicalDeleteAsync(
                documentId,
                expectedStateVersion,
                actor,
                cancellationToken);
            return Results.Accepted($"/api/knowledge/documents/{documentId}", new { documentId, state = "disabled" });
        }
        catch (DocumentNotFoundException) { return Results.NotFound(); }
        catch (DocumentConcurrencyException exception) { return ConcurrencyConflict(exception); }
        catch (DocumentDeleteRequestedException) { return Results.Conflict(new { error = "document-delete-requested" }); }
    }

    private static object ToResponse(DocumentUploadResult result) => new
    {
        result.DocumentId, result.VersionId, result.Version, result.State, result.PublicUrl,
        result.SafeFileName, result.SizeBytes, result.PublicReadRiskAccepted,
        publicReadWarning = "Document tags restrict robot retrieval only; this public object URL is not access control."
    };
    private static IResult MissingFile() => Results.ValidationProblem(Problem("file", "A multipart file is required."));
    private static IResult Duplicate() => Results.Conflict(new { error = "duplicate-content", message = "This document content already exists." });
    private static Dictionary<string, string[]> Problem(string key, string message) => new() { [key] = [message] };
    private static IResult ConcurrencyConflict(DocumentConcurrencyException exception) => Results.Conflict(new
    {
        error = "document-concurrency-conflict",
        current = exception.Current
    });
    private static bool TryGetActor(ClaimsPrincipal principal, out string actor)
    {
        actor = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.Identity?.Name
            ?? string.Empty;
        actor = actor.Trim();
        return actor.Length > 0;
    }
}
