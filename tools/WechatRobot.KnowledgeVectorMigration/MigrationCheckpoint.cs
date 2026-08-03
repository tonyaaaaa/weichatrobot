using System.Text.Json;
using System.Text.Json.Serialization;

namespace WechatRobot.KnowledgeVectorMigration;

[JsonConverter(typeof(JsonStringEnumConverter<MigrationStage>))]
public enum MigrationStage
{
    Planned,
    Copied,
    Verified,
    Switched,
    Accepted,
    RolledBack
}

public sealed class MigrationCheckpoint
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string State { get; set; } = "Planned";
    public List<VersionMigrationCheckpoint> Versions { get; init; } = [];
}

public sealed class VersionMigrationCheckpoint
{
    public Guid DocumentId { get; init; }
    public Guid VersionId { get; init; }
    public required string SourceCollection { get; init; }
    public bool SourceDocumentCollectionExclusive { get; init; }
    public string? SourceDocumentEmbeddingContractKey { get; init; }
    public required string SourceVersionCollection { get; init; }
    public bool SourceVersionCollectionExclusive { get; init; }
    public string? SourceVersionEmbeddingContractKey { get; init; }
    public required string DestinationCollection { get; init; }
    public required string DestinationContractKey { get; init; }
    public int Dimension { get; init; }
    public required string Distance { get; init; }
    public int Generation { get; init; }
    public int ExpectedPointCount { get; init; }
    public required string ExpectedMetadataHash { get; init; }
    public MigrationStage Stage { get; set; } = MigrationStage.Planned;
}

public static class MigrationCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<MigrationCheckpoint> LoadAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await JsonSerializer.DeserializeAsync<MigrationCheckpoint>(stream, JsonOptions, token)
            ?? throw new InvalidOperationException("The migration checkpoint is empty or invalid.");
    }

    public static async Task SaveAsync(string path, MigrationCheckpoint checkpoint, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The checkpoint path has no parent directory.");
        Directory.CreateDirectory(directory);
        checkpoint.UpdatedAtUtc = DateTime.UtcNow;
        var temporaryPath = fullPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, token);
            await stream.FlushAsync(token);
        }
        File.Move(temporaryPath, fullPath, true);
    }
}
