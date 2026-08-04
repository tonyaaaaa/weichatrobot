using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Messaging;

public sealed class DurableJobProviderBoundaryTests
{
    [Fact]
    public async Task Stale_send_recovery_does_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var now = DateTime.UtcNow;
        var timedOut = AddDispatchingCommand(database, 0);
        timedOut.Entity.Status = WorkToolCommandStatuses.Accepted;
        timedOut.Entity.AcceptedAtUtc = now.Subtract(TimeSpan.FromMinutes(11));
        timedOut.Entity.LeaseOwner = null;
        timedOut.Entity.LeaseExpiresAtUtc = null;
        timedOut.Entity.WorkToolCommandMessageId = "accepted-message";
        var expired = AddDispatchingCommand(database, 0);
        expired.Entity.LeaseExpiresAtUtc = now.Subtract(TimeSpan.FromSeconds(1));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await new DurableJobRepository(database).LeaseNextSendCommandAsync(
            "next-owner",
            now,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));

        database.ChangeTracker.Clear();
        var timedOutEntity = await CommandAsync(database, timedOut.Entity.Id);
        Assert.Equal(WorkToolCommandStatuses.ResultTimeout, timedOutEntity.Status);
        Assert.Equal(now, timedOutEntity.CompletedAtUtc);
        var expiredEntity = await CommandAsync(database, expired.Entity.Id);
        Assert.Equal(WorkToolCommandStatuses.DeliveryUnknown, expiredEntity.Status);
        Assert.Null(expiredEntity.LeaseOwner);
        Assert.Null((await database.RobotConfigs.SingleAsync(
            item => item.Id == expired.Entity.RobotConfigId,
            TestContext.Current.CancellationToken)).SendLeaseOwner);
    }

    [Fact]
    public async Task Send_terminal_transitions_do_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var unknown = AddDispatchingCommand(database, 0);
        var rejected = AddDispatchingCommand(database, 0);
        var accepted = AddDispatchingCommand(database, 0);
        var retrying = AddDispatchingCommand(database, 0);
        var deadLetter = AddDispatchingCommand(database, 3);
        var memory = new MemoryEntryEntity
        {
            GroupProfileId = Guid.NewGuid(),
            MemoryType = "fact",
            Content = "provider recall boundary",
            Status = "active"
        };
        accepted.Entity.PayloadJson = JsonSerializer.Serialize(new
        {
            GroupName = "provider boundary",
            Text = "accepted",
            MemoryRecallIds = new[] { memory.Id }
        });
        database.MemoryEntries.Add(memory);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new DurableJobRepository(database);
        var now = DateTime.UtcNow;

        await repository.MarkSendDeliveryUnknownAsync(
            unknown.Lease,
            "external dispatch lease expired",
            now,
            TestContext.Current.CancellationToken);
        await repository.MarkSendRejectedAsync(
            rejected.Lease,
            "upstream rejected",
            now,
            TestContext.Current.CancellationToken);
        await repository.MarkSendAcceptedAsync(
            accepted.Lease,
            "worktool-message-id",
            now,
            TestContext.Current.CancellationToken);
        await repository.FailSendCommandAsync(
            retrying.Lease,
            "temporary failure",
            now,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);
        await repository.FailSendCommandAsync(
            deadLetter.Lease,
            "terminal failure",
            now,
            null,
            TestContext.Current.CancellationToken);

        database.ChangeTracker.Clear();
        Assert.Equal(
            WorkToolCommandStatuses.DeliveryUnknown,
            (await CommandAsync(database, unknown.Entity.Id)).Status);
        Assert.Equal(
            WorkToolCommandStatuses.Rejected,
            (await CommandAsync(database, rejected.Entity.Id)).Status);
        var acceptedEntity = await CommandAsync(database, accepted.Entity.Id);
        Assert.Equal(WorkToolCommandStatuses.Accepted, acceptedEntity.Status);
        Assert.Equal("worktool-message-id", acceptedEntity.WorkToolCommandMessageId);
        Assert.Equal("retrying", (await CommandAsync(database, retrying.Entity.Id)).Status);
        Assert.Equal("deadLetter", (await CommandAsync(database, deadLetter.Entity.Id)).Status);
        Assert.All(
            await database.RobotConfigs.ToArrayAsync(TestContext.Current.CancellationToken),
            robot => Assert.Null(robot.SendLeaseOwner));
        Assert.Equal(
            1,
            (await database.MemoryEntries.SingleAsync(
                item => item.Id == memory.Id,
                TestContext.Current.CancellationToken)).RecallCount);
        Assert.True(await database.DeadLetters.AnyAsync(
            item => item.SendCommandId == deadLetter.Entity.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Durable_job_terminal_transitions_do_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var completed = AddLeasedJob(database, 0);
        var deferred = AddLeasedJob(database, 0);
        var failed = AddLeasedJob(database, 3);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new DurableJobRepository(database);
        var now = DateTime.UtcNow;

        await repository.CompleteJobAsync(
            completed.Job.Id,
            completed.Owner,
            now,
            TestContext.Current.CancellationToken);
        await repository.DeferJobAsync(
            Lease(deferred),
            "temporary provider failure",
            now,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);
        await repository.FailJobAsync(
            Lease(failed),
            "sanitized terminal failure",
            now,
            TestContext.Current.CancellationToken);

        database.ChangeTracker.Clear();
        Assert.Equal(
            "completed",
            (await database.DurableJobs.SingleAsync(
                item => item.Id == completed.Job.Id,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            "retrying",
            (await database.DurableJobs.SingleAsync(
                item => item.Id == deferred.Job.Id,
                TestContext.Current.CancellationToken)).Status);
        var deadLetterJob = await database.DurableJobs.SingleAsync(
            item => item.Id == failed.Job.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal("deadLetter", deadLetterJob.Status);
        Assert.Null(deadLetterJob.LeaseOwner);
        Assert.True(await database.DeadLetters.AnyAsync(
            item => item.DurableJobId == failed.Job.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            ["completed", "retrying", "deadLetter"],
            await database.ConversationMessages
                .OrderBy(item => item.ReceivedAtUtc)
                .Select(item => item.ProcessingState)
                .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    private static (DurableJobEntity Job, string Owner) AddLeasedJob(
        WechatRobotDbContext database,
        int attempts)
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var message = new ConversationMessageEntity
        {
            RobotConfigId = Guid.NewGuid(),
            FallbackHash = Guid.NewGuid().ToString("N"),
            GroupName = "provider boundary",
            SenderDisplayName = "user",
            Text = "test",
            ProcessingState = "leased",
            ReceivedAtUtc = DateTime.UtcNow.AddMilliseconds(database.ChangeTracker.Entries().Count())
        };
        var job = new DurableJobEntity
        {
            JobType = "ProviderBoundary",
            Status = "leased",
            LeaseOwner = owner,
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1),
            AttemptCount = attempts,
            RelatedConversationMessageId = message.Id,
            PayloadJson = "{}"
        };
        database.AddRange(message, job);
        return (job, owner);
    }

    private static (SendCommandEntity Entity, LeasedSendCommand Lease) AddDispatchingCommand(
        WechatRobotDbContext database,
        int attempts)
    {
        var owner = $"send-owner-{Guid.NewGuid():N}";
        var robot = new RobotConfigEntity
        {
            Name = $"provider-robot-{Guid.NewGuid():N}",
            WorkToolRobotId = Guid.NewGuid().ToString("N"),
            CallbackSecretHash = "hash",
            SendLeaseOwner = owner,
            SendLeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        };
        var entity = new SendCommandEntity
        {
            RobotConfigId = robot.Id,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadJson = JsonSerializer.Serialize(new
            {
                GroupName = "provider boundary",
                Text = "test"
            }),
            Status = WorkToolCommandStatuses.Dispatching,
            AttemptCount = attempts,
            LeaseOwner = owner,
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        };
        database.AddRange(robot, entity);
        return (
            entity,
            new LeasedSendCommand(
                entity.Id,
                robot.Id,
                robot.WorkToolRobotId,
                "provider boundary",
                "test",
                entity.IdempotencyKey,
                robot.SendRateLimitPerMinute,
                attempts,
                owner));
    }

    private static Task<SendCommandEntity> CommandAsync(
        WechatRobotDbContext database,
        Guid id) =>
        database.SendCommands.SingleAsync(
            item => item.Id == id,
            TestContext.Current.CancellationToken);

    private static LeasedDurableJob Lease((DurableJobEntity Job, string Owner) value) =>
        new(
            value.Job.Id,
            value.Job.JobType,
            value.Job.PayloadJson,
            value.Job.AttemptCount,
            value.Owner,
            value.Job.CreatedAtUtc);

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

    private sealed class ProviderWithoutBulkUpdateSupport : IDatabaseProvider
    {
        public string Name => "ProviderWithoutBulkUpdateSupport";

        public bool IsConfigured(IDbContextOptions options) => true;
    }
}
