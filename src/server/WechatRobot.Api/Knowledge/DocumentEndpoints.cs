using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Api.Knowledge;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var documents = endpoints.MapGroup("/api/knowledge/documents").RequireAuthorization(SystemRoles.KnowledgeOperator);
        documents.MapPost("", UploadAsync).DisableAntiforgery();
        documents.MapPost("/{documentId:guid}/retry-upload", RetryAsync);
        documents.MapDelete("/{documentId:guid}/physical", RequestPhysicalDeleteAsync).RequireAuthorization(SystemRoles.Admin);
        return endpoints;
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

    private static async Task<IResult> RetryAsync(Guid documentId, DocumentUploadService service, CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.RetryAsync(documentId, cancellationToken);
            return result.ProviderSucceeded ? Results.Ok(ToResponse(result)) : Results.Json(ToResponse(result), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (DocumentDeletedException) { return Results.Conflict(new { error = "document-deleted" }); }
        catch (DocumentNotRetryableException) { return Results.NotFound(); }
    }

    private static async Task<IResult> RequestPhysicalDeleteAsync(Guid documentId, DocumentUploadService service, CancellationToken cancellationToken)
    {
        try
        {
            await service.RequestPhysicalDeleteAsync(documentId, cancellationToken);
            return Results.Accepted($"/api/knowledge/documents/{documentId}", new { documentId, state = "disabled" });
        }
        catch (DocumentNotFoundException) { return Results.NotFound(); }
    }

    private static object ToResponse(DocumentUploadResult result) => new
    {
        result.DocumentId, result.VersionId, result.Version, result.State, result.PublicUrl, result.ObjectKey,
        result.SafeFileName, result.Sha256, result.SizeBytes, result.PublicReadRiskAccepted,
        publicReadWarning = "Document tags restrict robot retrieval only; this public object URL is not access control."
    };
    private static IResult MissingFile() => Results.ValidationProblem(Problem("file", "A multipart file is required."));
    private static IResult Duplicate() => Results.Conflict(new { error = "duplicate-content", message = "This document content already exists." });
    private static Dictionary<string, string[]> Problem(string key, string message) => new() { [key] = [message] };
}
