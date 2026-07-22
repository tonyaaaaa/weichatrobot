using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class DocumentParsingOptions
{
    public const string SectionName = "DocumentParsing";
    public long MaximumSourceBytes { get; set; } = 20 * 1024 * 1024;
    public int MaximumPages { get; set; } = 500;
    public long MaximumMemoryBytes { get; set; } = 64 * 1024 * 1024;
    public int ExecutionTimeoutSeconds { get; set; } = 30;
    public int MaximumPageCharacters { get; set; } = 1_000_000;
    public long MaximumExpandedEntryBytes { get; set; } = 16 * 1024 * 1024;
    public long MaximumResultCharacters { get; set; } = 10_000_000;
}

public sealed class KnowledgePreviewService(
    WechatRobotDbContext database,
    IDocumentSourceReader sourceReader,
    DocumentParserSelector selector,
    ChunkingService chunking,
    ChunkPreviewRepository repository,
    DocumentParsingOptions options,
    TimeProvider timeProvider,
    ScannedPdfOcrService? ocr = null)
{
    public async Task<ChunkPreviewSet> GenerateAsync(Guid versionId, ChunkPolicy policy, int expectedRevision, CancellationToken cancellationToken)
    {
        var version = await database.KnowledgeDocumentVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == versionId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (version.Status is not ("uploaded" or "preview")) throw new ChunkPreviewStateException();
        if (!Uri.TryCreate(version.PublicUrl, UriKind.Absolute, out var url)) throw new ChunkPreviewStateException();
        var limits = new DocumentParsingLimits(options.MaximumSourceBytes, options.MaximumPages, options.MaximumMemoryBytes, TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds))
        {
            MaximumPageCharacters = options.MaximumPageCharacters,
            MaximumExpandedEntryBytes = options.MaximumExpandedEntryBytes,
            MaximumResultCharacters = options.MaximumResultCharacters
        };
        using var context = new DocumentProcessingContext(limits, cancellationToken, timeProvider);
        await using var source = await sourceReader.OpenReadAsync(url, context);
        context.Checkpoint("parse-start");
        ParsedDocument parsed;
        try
        {
            parsed = await selector.Select(version.ContentType).ParseAsync(source, version.ContentType, context);
        }
        catch (DocumentParsingException exception) when (version.ContentType == "application/pdf" &&
            exception.Error == DocumentParsingError.EmptyTextPdf && ocr is not null)
        {
            parsed = await ocr.RecognizeAsync(version.Id, source, context);
        }
        if (version.ContentType == "application/pdf" && ocr is not null && ocr.ShouldFallback(parsed))
            parsed = await ocr.RecognizeAsync(version.Id, source, context);
        context.Checkpoint("chunk-start");
        var previews = chunking.Generate(parsed.Blocks, policy, context);
        context.Checkpoint("preview-persist");
        return await repository.ReplaceAsync(versionId, previews, expectedRevision, context.Token);
    }

    public async Task<bool> GenerateFromJobAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ParseJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (payload is null || payload.VersionId == Guid.Empty) throw new InvalidOperationException("Parse job version is missing.");
        var version = await database.KnowledgeDocumentVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == payload.VersionId, cancellationToken);
        if (version is null || version.Status is "disabled" or "approved" or "preview") return true;
        if (version.Status != "uploaded") return false;
        await GenerateAsync(version.Id, new ChunkPolicy(ChunkPolicyKind.Smart), version.PreviewRevision, cancellationToken);
        return true;
    }

    private sealed class ParseJobPayload { public Guid VersionId { get; init; } }
}
