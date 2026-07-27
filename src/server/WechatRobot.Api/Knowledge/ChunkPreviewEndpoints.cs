using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.Api.Knowledge;

public static class ChunkPreviewEndpoints
{
    public static IEndpointRouteBuilder MapChunkPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/knowledge/versions/{versionId:guid}/previews").RequireAuthorization(SystemRoles.KnowledgeOperator);
        group.MapGet("", async (Guid versionId, ChunkPreviewRepository repository, CancellationToken token) => await ExecuteAsync(() => repository.GetAsync(versionId, token)));
        group.MapPost("/generate", GenerateAsync);
        group.MapPut("/{previewId:guid}", EditAsync);
        group.MapPost("/{previewId:guid}/split", SplitAsync);
        group.MapPost("/merge", MergeAsync);
        group.MapDelete("/{previewId:guid}", DeleteAsync);
        group.MapPost("/approve", ApproveAsync);
        return endpoints;
    }

    private static async Task<IResult> GenerateAsync(Guid versionId, GeneratePreviewRequest request, KnowledgePreviewService service, CancellationToken token) =>
        await ExecuteAsync(() => service.GenerateAsync(
            versionId,
            request.Policy?.ToPolicy() ?? new ChunkPolicy(ChunkPolicyKind.Smart),
            request.ExpectedRevision,
            token));
    private static async Task<IResult> EditAsync(Guid versionId, Guid previewId, EditPreviewRequest request, ChunkPreviewRepository repository, CancellationToken token) =>
        await ExecuteAsync(() => repository.EditAsync(versionId, previewId, request.Text, request.ExpectedRevision, token));
    private static async Task<IResult> SplitAsync(Guid versionId, Guid previewId, SplitPreviewRequest request, ChunkPreviewRepository repository, CancellationToken token) =>
        await ExecuteAsync(() => repository.SplitAsync(versionId, previewId, request.Offset, request.ExpectedRevision, token));
    private static async Task<IResult> MergeAsync(Guid versionId, MergePreviewRequest request, ChunkPreviewRepository repository, CancellationToken token) =>
        await ExecuteAsync(() => repository.MergeAsync(versionId, request.FirstId, request.SecondId, request.ExpectedRevision, token));
    private static async Task<IResult> DeleteAsync(Guid versionId, Guid previewId, int expectedRevision, ChunkPreviewRepository repository, CancellationToken token) =>
        await ExecuteAsync(() => repository.DeleteAsync(versionId, previewId, expectedRevision, token));
    private static async Task<IResult> ApproveAsync(Guid versionId, ApprovePreviewRequest request, ChunkPreviewRepository repository, CancellationToken token) =>
        await ExecuteAsync(async () => (await repository.ApproveAsync(versionId, request.ExpectedRevision, token))
            .Select(item => new ApprovedChunkResponse(item.Id, item.Sequence, item.Text, item.PageNumber, item.Status)).ToArray());
    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ChunkPreviewConcurrencyException) { return Results.Conflict(new { error = "preview-revision-conflict" }); }
        catch (ChunkPreviewStateException) { return Results.Conflict(new { error = "preview-state-conflict" }); }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = [exception.Message] }); }
        catch (InvalidOperationException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = [exception.Message] }); }
        catch (DocumentParsingException exception) { return Results.UnprocessableEntity(new { error = exception.Error.ToString(), message = exception.Message }); }
    }
}

public sealed record GeneratePreviewRequest(ChunkPolicyRequest? Policy, int ExpectedRevision);
public sealed record QaEntryRequest(string Question, IReadOnlyList<string>? Synonyms, string Answer)
{
    public QaEntry ToEntry() => new(Question, Synonyms ?? [], Answer);
}
public sealed record ChunkPolicyRequest(
    string Kind,
    int TargetTokens = 800,
    int OverlapTokens = 120,
    int MaximumTokens = 1000,
    string? Separator = null,
    string? RegexPattern = null,
    IReadOnlyList<QaEntryRequest>? QaEntries = null)
{
    public ChunkPolicy ToPolicy()
    {
        var normalized = Kind?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "smart" when Separator is null && RegexPattern is null && QaEntries is null =>
                New(ChunkPolicyKind.Smart),
            "separator" when RegexPattern is null && QaEntries is null =>
                New(ChunkPolicyKind.Separator),
            "regex" when Separator is null && QaEntries is null =>
                New(ChunkPolicyKind.Regex),
            "qa" when Separator is null && RegexPattern is null =>
                New(ChunkPolicyKind.Qa),
            "smart" or "separator" or "regex" or "qa" =>
                throw new ArgumentException("Chunk policy contains fields that do not belong to its kind."),
            _ => throw new ArgumentException("Chunk policy kind must be smart, separator, regex, or qa.")
        };
    }

    private ChunkPolicy New(ChunkPolicyKind kind) => new(
        kind,
        TargetTokens,
        OverlapTokens,
        MaximumTokens,
        Separator,
        RegexPattern,
        QaEntries?.Select(entry => entry.ToEntry()).ToArray());
}
public sealed record EditPreviewRequest(string Text, int ExpectedRevision);
public sealed record SplitPreviewRequest(int Offset, int ExpectedRevision);
public sealed record MergePreviewRequest(Guid FirstId, Guid SecondId, int ExpectedRevision);
public sealed record ApprovePreviewRequest(int ExpectedRevision);
public sealed record ApprovedChunkResponse(Guid Id, int Sequence, string Text, int? PageNumber, string Status);
