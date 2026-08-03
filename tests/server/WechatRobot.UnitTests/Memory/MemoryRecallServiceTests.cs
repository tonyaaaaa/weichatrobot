using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Memory;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Memory;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.UnitTests.Memory;

public sealed class MemoryRecallServiceTests
{
    [Fact]
    public async Task Twenty_mixed_scope_hits_preserve_scope_priority_and_sender_isolation()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var database = new WechatRobotDbContext(options);
        var now = DateTime.UtcNow;
        var robotId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var otherRobotId = Guid.NewGuid();
        var otherGroupId = Guid.NewGuid();
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
        var entries = new List<MemoryEntryEntity>
        {
            Entry("Global", "global", now),
            Entry("Robot", "robot", now, robotId),
            Entry("Group", "group", now, robotId, groupId),
            Entry("User", "user-alice", now, robotId, groupId, "alice"),
            Entry("Robot", "wrong-robot", now, otherRobotId),
            Entry("Group", "wrong-group", now, robotId, otherGroupId),
            Entry("User", "wrong-user", now, robotId, groupId, "bob"),
            Entry("Global", "expired", now, expiresAtUtc: now.AddMinutes(-1)),
            Entry("Global", "inactive", now, status: "inactive")
        };
        while (entries.Count < 20) entries.Add(Entry("Global", $"global-{entries.Count}", now));
        database.MemoryEntries.AddRange(entries);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var index = new StubVectorIndex(entries.Select((entry, index) =>
            new MemoryVectorHit(entry.Id, 1 - index / 100d)).ToArray());
        var service = new MemoryRecallService(
            database,
            index,
            new StubEmbeddingClient(),
            new ModelConfigurationService(new PassThroughProtector()),
            TimeProvider.System);

        var result = await service.RecallAsync(
            "日本签证材料",
            robotId,
            groupId,
            " Alice ",
            TestContext.Current.CancellationToken);

        Assert.Null(result.FailureCode);
        Assert.Equal(20, index.RequestedLimit);
        Assert.Equal(5, result.Memories.Count);
        Assert.Equal(["User", "Group", "Robot"], result.Memories.Take(3).Select(item => item.ScopeType));
        Assert.DoesNotContain(result.Memories, item => item.Content is "wrong-robot" or "wrong-group" or "wrong-user" or "expired" or "inactive");

        var anonymous = await service.RecallAsync(
            "日本签证材料",
            robotId,
            groupId,
            null,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(anonymous.Memories, item => item.ScopeType == "User");
    }

    private static MemoryEntryEntity Entry(
        string scope,
        string content,
        DateTime now,
        Guid? robotId = null,
        Guid? groupId = null,
        string? subject = null,
        DateTime? expiresAtUtc = null,
        string status = "active") => new()
    {
        ScopeType = scope,
        RobotConfigId = robotId,
        GroupProfileId = groupId,
        SubjectKey = subject,
        MemoryType = "BusinessFact",
        Content = content,
        NormalizedKey = content,
        Confidence = 1,
        Status = status,
        ValidFromUtc = now,
        ExpiresAtUtc = expiresAtUtc,
        StatusVersion = 1,
        IndexGeneration = 1,
        Version = 1,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private sealed class StubVectorIndex(IReadOnlyList<MemoryVectorHit> hits) : IMemoryVectorIndex
    {
        public int RequestedLimit { get; private set; }
        public Task<IReadOnlyList<MemoryVectorHit>> SearchAsync(IReadOnlyList<float> vector, int dimension, VectorDistance distance, int generation, int limit, CancellationToken cancellationToken = default)
        {
            RequestedLimit = limit;
            return Task.FromResult(hits);
        }
        public Task IndexAsync(MemoryVectorDocument document, int dimension, VectorDistance distance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(Guid memoryEntryId, int dimension, VectorDistance distance, int generation, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(ModelProviderConfiguration configuration, EmbeddingBatchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingBatchResponse>(new([[.1f, .2f]]));
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
