using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Handoffs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Groups;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeLifecycleProviderBoundaryTests
{
    [Fact]
    public async Task Handoff_assignment_uses_tracked_concurrency_transition()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var handoff = new HandoffCaseEntity
        {
            QuestionMessageId = Guid.NewGuid(),
            RobotConfigId = Guid.NewGuid(),
            GroupProfileId = Guid.NewGuid(),
            State = "WaitingHuman",
            ReasonCode = "manual_transfer",
            EvidenceJson = "{}",
            PauseScope = HandoffPauseScope.Group.ToString(),
            Version = 0,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        database.HandoffCases.Add(handoff);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var assignee = Guid.NewGuid();

        var updated = await new EfHandoffStore(database).AssignAsync(
            handoff.Id,
            Guid.NewGuid(),
            assignee,
            0,
            DateTime.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.Equal("HumanHandling", updated.State);
        Assert.Equal(assignee, updated.AssigneeUserId);
        Assert.Equal(1, updated.Version);
        Assert.Single(await database.HandoffTransitions.ToArrayAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Group_disable_does_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var group = new GroupProfileEntity
        {
            RobotConfigId = Guid.NewGuid(),
            Name = "provider group boundary",
            StateVersion = 2
        };
        var memoryJob = new DurableJobEntity
        {
            JobType = "ExtractConversationMemory",
            GroupProfileId = group.Id,
            Status = "retrying",
            LeaseOwner = "memory-owner",
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1),
            PayloadJson = "{}"
        };
        database.AddRange(group, memoryJob);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var update = await new EfGroupLifecycleStore(database, TimeProvider.System)
            .TryUpdateAsync(
                group.Id,
                2,
                false,
                null,
                TestContext.Current.CancellationToken);

        Assert.True(update.Updated);
        Assert.False(group.IsEnabled);
        Assert.Equal(3, group.StateVersion);
        Assert.Equal("cancelled", memoryJob.Status);
        Assert.Null(memoryJob.LeaseOwner);
        Assert.Null(memoryJob.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Document_disable_does_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        database.AddRange(
            new KnowledgeDocumentEntity
            {
                Id = documentId,
                Title = "provider disable boundary",
                Status = "active",
                ActiveVersionId = versionId,
                ActiveCollectionName = "kb_provider_boundary",
                ActiveEmbeddingDimension = 3,
                ActiveDistance = "cosine",
                StateVersion = 4
            },
            new KnowledgeDocumentVersionEntity
            {
                Id = versionId,
                KnowledgeDocumentId = documentId,
                Version = 1,
                OriginalFileName = "provider-disable.txt",
                SafeFileName = "provider-disable.txt",
                ContentType = "text/plain",
                Sha256 = "f".PadLeft(64, '0'),
                ObjectKey = "wechatrobot/knowledge/provider-disable.txt",
                Status = "active",
                IsPublished = true
            },
            new KnowledgeIndexJobEntity
            {
                KnowledgeDocumentId = documentId,
                KnowledgeDocumentVersionId = versionId,
                Operation = "index",
                CollectionName = "kb_provider_boundary",
                Dimension = 3,
                Distance = "cosine",
                Status = "leased",
                LeaseOwner = "index-owner"
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(database).DisableAsync(
            documentId,
            4,
            "admin",
            TestContext.Current.CancellationToken);

        var document = await database.KnowledgeDocuments.SingleAsync(
            item => item.Id == documentId,
            TestContext.Current.CancellationToken);
        Assert.Equal("disabled", document.Status);
        Assert.Null(document.ActiveVersionId);
        Assert.Equal(5, document.StateVersion);
        Assert.Equal(
            "disabled",
            (await database.KnowledgeDocumentVersions.SingleAsync(
                item => item.Id == versionId,
                TestContext.Current.CancellationToken)).Status);
        var indexJob = await database.KnowledgeIndexJobs.SingleAsync(
            item => item.Operation == "index",
            TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", indexJob.Status);
        Assert.Null(indexJob.LeaseOwner);
    }

    [Fact]
    public async Task Cleanup_completion_does_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var job = new KnowledgeIndexJobEntity
        {
            KnowledgeDocumentId = Guid.NewGuid(),
            KnowledgeDocumentVersionId = Guid.NewGuid(),
            Operation = "cleanup",
            CollectionName = "kb_cleanup_boundary",
            Dimension = 3,
            Distance = "cosine",
            Status = "leased",
            LeaseOwner = "cleanup-owner",
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        };
        database.KnowledgeIndexJobs.Add(job);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Service(database).CompleteCleanupAsync(
            job.Id,
            "cleanup-owner",
            TestContext.Current.CancellationToken);

        var completed = await database.KnowledgeIndexJobs.SingleAsync(
            item => item.Id == job.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal("completed", completed.Status);
        Assert.Null(completed.LeaseOwner);
        Assert.Null(completed.LeaseExpiresAtUtc);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder =>
            builder.UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ReplaceService<IDatabaseProvider, ProviderWithoutBulkUpdateSupport>()
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        return services;
    }

    private static QdrantKnowledgeService Service(WechatRobotDbContext database) =>
        new(
            database,
            new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine),
            TimeProvider.System);

    private sealed class ProviderWithoutBulkUpdateSupport : IDatabaseProvider
    {
        public string Name => "ProviderWithoutBulkUpdateSupport";

        public bool IsConfigured(IDbContextOptions options) => true;
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => protectedValue;
    }
}
