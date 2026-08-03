using System.Text.Json;

namespace WechatRobot.LegacyVisaImport;

public sealed record KnowledgeTagOption(Guid Id, string Name, bool IsGlobalPublic);
public sealed record KnowledgeDocumentMatch(Guid Id, string Title, string Status = "active");
public sealed record LegacyImportDecision(string Action, Guid? DocumentId);
public sealed record LegacyImportCheckpointEntry(
    string Sha256,
    Guid DocumentId,
    Guid VersionId,
    string State);

public sealed class LegacyImportCheckpoint
{
    public Dictionary<string, LegacyImportCheckpointEntry> Entries { get; init; } =
        new(StringComparer.Ordinal);
}

public static class LegacyVisaImportPlanner
{
    private const int MaximumIndexRecoveryAttempts = 3;

    public static bool CanRetryIndex(int attempts) =>
        attempts >= 0 && attempts < MaximumIndexRecoveryAttempts;

    public static LegacyImportCheckpointEntry? ResolveSourceDuplicate(
        RenderedVisaDocument rendered,
        IReadOnlyDictionary<string, LegacyImportCheckpointEntry> resolvedBySha) =>
        resolvedBySha.TryGetValue(rendered.Sha256, out var resolved)
            ? resolved
            : null;

    public static string ResumeState(string versionStatus) => versionStatus switch
    {
        "uploading" or "uploaded" or "preview" => "uploaded",
        "failed" => "failed",
        "approved" => "approved",
        "indexing" or "active" => "indexing",
        _ => throw new InvalidOperationException(
            $"duplicate_version_not_resumable:{versionStatus}")
    };

    public static Guid ResolveTag(
        IEnumerable<KnowledgeTagOption> tags,
        string exactName)
    {
        var matches = tags.Where(tag =>
                string.Equals(tag.Name, exactName, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0].Id
            : throw new InvalidOperationException("required_knowledge_tag_not_unique");
    }

    public static LegacyImportDecision Decide(
        RenderedVisaDocument rendered,
        IEnumerable<KnowledgeDocumentMatch> documents,
        LegacyImportCheckpointEntry? checkpoint)
    {
        var matches = documents.Where(document =>
                !string.Equals(document.Status, "deleted", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(document.Title, rendered.FileName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("stable_document_name_not_unique");

        if (checkpoint is not null
            && checkpoint.Sha256 == rendered.Sha256
            && matches.Any(document => document.Id == checkpoint.DocumentId))
        {
            if (checkpoint.State == "consistent") return new("skip", checkpoint.DocumentId);
            if (checkpoint.State == "approved") return new("resume-approved", checkpoint.DocumentId);
            if (checkpoint.State == "uploaded") return new("resume-uploaded", checkpoint.DocumentId);
            if (checkpoint.State == "indexing") return new("resume-indexing", checkpoint.DocumentId);
        }

        return matches.Length == 1
            ? new("update", matches[0].Id)
            : new("create", null);
    }
}

public static class LegacyImportCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<LegacyImportCheckpoint> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LegacyImportCheckpoint>(
                   stream, JsonOptions, cancellationToken)
               ?? new LegacyImportCheckpoint();
    }

    public static async Task SaveAsync(
        string path,
        LegacyImportCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken);
        File.Move(temporary, path, true);
    }
}
