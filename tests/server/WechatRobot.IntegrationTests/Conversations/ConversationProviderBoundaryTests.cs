using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Conversations;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Conversations;

public sealed class ConversationProviderBoundaryTests
{
    [Fact]
    public async Task No_reply_terminal_does_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var message = NewInboundMessage();
        database.ConversationMessages.Add(message);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Repository(database).PersistNoReplyTerminalAsync(
            new InboundPolicyDecision(
                message.Id,
                InboundPolicyDecisionKind.NoReply,
                null,
                "group_unmatched",
                "{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal("completed", message.ProcessingState);
        Assert.Equal("no_reply", message.TerminalDecision);
        Assert.Equal("group_unmatched", message.TerminalReason);
        Assert.Null(message.ConversationSessionId);
        Assert.Null(message.SessionSequence);
    }

    [Fact]
    public async Task Lease_release_does_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var session = new ConversationSessionEntity
        {
            GroupProfileId = Guid.NewGuid(),
            SenderScopeKey = "group",
            LeaseOwner = "conversation-owner",
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1),
            LastActivityAtUtc = DateTime.UtcNow
        };
        database.ConversationSessions.Add(session);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Repository(database).ReleaseLeaseAsync(
            session.Id,
            "conversation-owner",
            TestContext.Current.CancellationToken);

        Assert.Null(session.LeaseOwner);
        Assert.Null(session.LeaseExpiresAtUtc);
        Assert.Equal(1, session.Version);
    }

    [Fact]
    public async Task Group_context_clear_does_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var groupId = Guid.NewGuid();
        var session = new ConversationSessionEntity
        {
            GroupProfileId = groupId,
            SenderScopeKey = "group",
            Summary = "old summary",
            NextSequence = 8,
            LeaseOwner = "conversation-owner",
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1),
            LastActivityAtUtc = DateTime.UtcNow
        };
        database.ConversationSessions.Add(session);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var clearedAt = DateTime.UtcNow;

        Assert.Equal(
            1,
            await Repository(database).ClearGroupContextAsync(
                groupId,
                clearedAt,
                TestContext.Current.CancellationToken));

        Assert.Equal(clearedAt, session.ClearedAtUtc);
        Assert.Equal(8, session.ClearedThroughSequence);
        Assert.Null(session.Summary);
        Assert.Null(session.LeaseOwner);
        Assert.Null(session.LeaseExpiresAtUtc);
    }

    private static ConversationMessageEntity NewInboundMessage() => new()
    {
        RobotConfigId = Guid.NewGuid(),
        FallbackHash = Guid.NewGuid().ToString("N"),
        GroupName = "provider boundary",
        SenderDisplayName = "user",
        Text = "hello",
        Direction = "inbound",
        ProcessingState = "pending",
        ConversationSessionId = Guid.NewGuid(),
        SessionSequence = 3,
        ReceivedAtUtc = DateTime.UtcNow
    };

    private static GroundedConversationRepository Repository(WechatRobotDbContext database) =>
        new(
            database,
            new ModelConfigurationService(new PassThroughProtector()),
            TimeProvider.System);

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

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => protectedValue;
    }
}
