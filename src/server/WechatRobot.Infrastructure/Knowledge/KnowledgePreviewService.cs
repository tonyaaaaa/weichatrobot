using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class DocumentParsingOptions
{
    public const string SectionName = "DocumentParsing";
    public long MaximumSourceBytes { get; set; } = 20 * 1024 * 1024;
    public int MaximumPages { get; set; } = 500;
    public long MaximumMemoryBytes { get; set; } = 64 * 1024 * 1024;
    public int ExecutionTimeoutSeconds { get; set; } = 30;
}

public sealed class KnowledgePreviewService(
    WechatRobotDbContext database,
    IDocumentSourceReader sourceReader,
    DocumentParserSelector selector,
    ChunkingService chunking,
    ChunkPreviewRepository repository,
    DocumentParsingOptions options)
{
    public async Task<ChunkPreviewSet> GenerateAsync(Guid versionId, ChunkPolicy policy, int expectedRevision, CancellationToken cancellationToken)
    {
        var version = await database.KnowledgeDocumentVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == versionId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (version.Status is not ("uploaded" or "preview")) throw new ChunkPreviewStateException();
        if (!Uri.TryCreate(version.PublicUrl, UriKind.Absolute, out var url)) throw new ChunkPreviewStateException();
        var limits = new DocumentParsingLimits(options.MaximumSourceBytes, options.MaximumPages, options.MaximumMemoryBytes, TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds));
        await using var source = await sourceReader.OpenReadAsync(url, limits.MaximumSourceBytes, cancellationToken);
        var parsed = await selector.Select(version.ContentType).ParseAsync(source, version.ContentType, limits, cancellationToken);
        return await repository.ReplaceAsync(versionId, chunking.Generate(parsed.Blocks, policy), expectedRevision, cancellationToken);
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
