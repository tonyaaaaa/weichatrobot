using WechatRobot.Application.Knowledge;

namespace WechatRobot.KnowledgeVectorMigration;

public sealed record ActiveVectorMapping(
    Guid DocumentId,
    Guid VersionId,
    string SourceCollection,
    bool SourceCollectionExclusive,
    int Dimension,
    VectorDistance Distance,
    int Generation,
    string Provider,
    string BaseUrl,
    string Model);

public sealed record PlannedVectorVersion(
    ActiveVectorMapping Source,
    EmbeddingSpaceContract Contract)
{
    public string DestinationCollection => Contract.CollectionName;
}

public sealed record KnowledgeVectorMigrationPlan(
    IReadOnlyList<PlannedVectorVersion> Versions,
    IReadOnlyList<EmbeddingSpaceContract> Destinations);

public sealed record VersionVerification(int Expected, int Actual, bool MetadataMatches);

public sealed class KnowledgeVectorMigrationPlanner
{
    public KnowledgeVectorMigrationPlan Build(IReadOnlyList<ActiveVectorMapping> activeVersions)
    {
        var versions = activeVersions.Select(source => new PlannedVectorVersion(
            source,
            EmbeddingSpaceContract.Create(
                source.Provider,
                source.BaseUrl,
                source.Model,
                source.Dimension,
                source.Distance))).ToArray();
        if (versions.Any(item => string.Equals(
                item.Source.SourceCollection,
                item.DestinationCollection,
                StringComparison.Ordinal)))
            throw new InvalidOperationException("The migration plan contains an already shared source mapping.");

        var destinations = versions
            .Select(item => item.Contract)
            .DistinctBy(item => item.Key)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        return new(versions, destinations);
    }

    public bool CanSwitch(IReadOnlyList<VersionVerification> verification) =>
        verification.Count > 0
        && verification.All(item => item.Expected == item.Actual && item.MetadataMatches);
}
