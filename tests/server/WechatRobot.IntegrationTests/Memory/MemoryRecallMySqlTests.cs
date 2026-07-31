using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Memory;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Memory;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Memory;

public sealed class MemoryRecallMySqlTests(MySqlFixture fixture)
    : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Recall_filters_vector_hit_guids_on_mysql()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options;
        await using var database = new WechatRobotDbContext(options);
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        var memoryId = Guid.NewGuid();
        database.ModelConfigs.Add(new ModelConfigEntity
        {
            Name = "memory-embedding",
            NormalizedName = "MEMORY-EMBEDDING",
            Provider = "test",
            ConfigurationType = "embedding",
            BaseUrl = "https://embedding.test/v1",
            Model = "embedding-test",
            EmbeddingDimension = 2,
            IsEnabled = true,
            IsDefault = true
        });
        database.MemoryEntries.Add(new MemoryEntryEntity
        {
            Id = memoryId,
            ScopeType = "Global",
            MemoryType = "BusinessFact",
            Content = "日本三年签证可以办理。",
            NormalizedKey = "日本三年签证",
            Confidence = 1,
            Status = "active",
            ValidFromUtc = now,
            StatusVersion = 1,
            IndexGeneration = 1,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new MemoryRecallService(
            database,
            new StubVectorIndex(memoryId),
            new StubEmbeddingClient(),
            new ModelConfigurationService(new PassThroughProtector()),
            TimeProvider.System);

        var result = await service.RecallAsync(
            "日本三年签证能办吗？",
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            TestContext.Current.CancellationToken);

        Assert.Null(result.FailureCode);
        Assert.Equal(memoryId, Assert.Single(result.Memories).Id);
    }

    private sealed class StubVectorIndex(Guid memoryId) : IMemoryVectorIndex
    {
        public Task IndexAsync(MemoryVectorDocument document, int dimension, VectorDistance distance, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<MemoryVectorHit>> SearchAsync(
            IReadOnlyList<float> vector,
            int dimension,
            VectorDistance distance,
            int generation,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryVectorHit>>([new(memoryId, .99)]);

        public Task RemoveAsync(Guid memoryEntryId, int dimension, VectorDistance distance, int generation, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(
            ModelProviderConfiguration configuration,
            EmbeddingBatchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingBatchResponse>(new([[.1f, .2f]]));
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
