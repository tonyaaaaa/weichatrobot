using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.KnowledgeVectorMigration;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class KnowledgeVectorMigrationRunnerTests
{
    [Fact]
    public async Task Apply_verify_and_rollback_preserve_vector_metadata_and_restore_legacy_mapping()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var database = new WechatRobotDbContext(options);
        var model = new ModelConfigEntity
        {
            Name = "embedding",
            NormalizedName = "EMBEDDING",
            Provider = "glm",
            ConfigurationType = "embedding",
            BaseUrl = "https://embedding.example.test/v1",
            Model = "embedding-3",
            EmbeddingDimension = 3,
            IsDefault = true
        };
        var document = new KnowledgeDocumentEntity
        {
            Status = "active",
            ActiveCollectionName = "kb_cosine_3_g1_legacy",
            ActiveEmbeddingDimension = 3,
            ActiveDistance = "cosine",
            ActiveIndexGeneration = 1,
            ActiveCollectionExclusive = true
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "visa.md",
            SafeFileName = "visa.md",
            ContentType = "text/markdown",
            Sha256 = new string('a', 64),
            ObjectKey = "knowledge/visa.md",
            Status = "active",
            IsPublished = true,
            IndexCollectionName = document.ActiveCollectionName,
            EmbeddingDimension = 3,
            VectorDistance = "cosine",
            IndexGeneration = 1,
            IndexCollectionExclusive = true
        };
        document.ActiveVersionId = version.Id;
        database.AddRange(model, document, version);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var source = new VectorCollection(document.ActiveCollectionName, 3, VectorDistance.Cosine);
        var point = new VectorPoint(Guid.NewGuid(), document.Id, version.Id, [Guid.NewGuid()], [1, 0, 0], true, 1);
        var vectors = new InMemoryMigrationVectorStore(source, point, transientInspectFailures: 1);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "WechatRobotTests", Guid.NewGuid().ToString("N"));
        var checkpointPath = Path.Combine(temporaryDirectory, "checkpoint.json");
        try
        {
            var runner = new KnowledgeVectorMigrationRunner(
                database, vectors, new KnowledgeVectorMigrationPlanner(), checkpointPath);

            var dryRun = await runner.DryRunAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Planned", dryRun.State);
            Assert.Equal(1, dryRun.PointCount);
            var checkpoint = await MigrationCheckpointStore.LoadAsync(checkpointPath, TestContext.Current.CancellationToken);

            var applied = await runner.ApplyAsync(checkpoint, TestContext.Current.CancellationToken);
            Assert.Equal("Switched", applied.State);
            Assert.True(EmbeddingSpaceContract.IsSharedCollectionName(document.ActiveCollectionName));
            Assert.False(document.ActiveCollectionExclusive);

            var verified = await runner.VerifyAsync(checkpoint, TestContext.Current.CancellationToken);
            Assert.Equal("Accepted", verified.State);

            var rolledBack = await runner.RollbackAsync(checkpoint, TestContext.Current.CancellationToken);
            Assert.Equal("RolledBack", rolledBack.State);
            Assert.Equal(source.Name, document.ActiveCollectionName);
            Assert.True(document.ActiveCollectionExclusive);
            Assert.Equal(source.Name, version.IndexCollectionName);
            Assert.True(version.IndexCollectionExclusive);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
        }
    }

    private sealed class InMemoryMigrationVectorStore(
        VectorCollection source,
        VectorPoint point,
        int transientInspectFailures = 0) : IVectorStore
    {
        private int _remainingInspectFailures = transientInspectFailures;
        private readonly Dictionary<string, Dictionary<Guid, VectorPoint>> _collections = new(StringComparer.Ordinal)
        {
            [source.Name] = new() { [point.Id] = point }
        };

        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken cancellationToken)
        {
            _collections.TryAdd(collection.Name, []);
            return Task.CompletedTask;
        }

        public Task EnsurePayloadIndexesAsync(VectorCollection collection, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken)
        {
            var destination = _collections[collection.Name];
            foreach (var value in points) destination[value.Id] = value;
            return Task.CompletedTask;
        }

        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken cancellationToken)
        {
            if (_remainingInspectFailures-- > 0)
                throw new VectorStoreUnavailableException("transient test failure");
            return Task.FromResult<VectorCollection?>(_collections.ContainsKey(collectionName)
                ? collectionName == source.Name ? source : new(collectionName, source.Dimension, source.Distance)
                : null);
        }

        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>(_collections[collection.Name].Values
                .Where(value => value.VersionId == versionId)
                .Select(value => new VectorPointMetadata(value.Id, value.DocumentId, value.VersionId, value.TagIds, value.Active, value.Generation))
                .ToArray());

        public Task<VectorPointPage> ReadVersionPointsAsync(VectorCollection collection, Guid versionId, string? offset, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new VectorPointPage(_collections[collection.Name].Values.Where(value => value.VersionId == versionId).ToArray(), null));

        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
