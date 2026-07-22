using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Conversations;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Conversations;

public sealed class ConversationSessionConcurrencyTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture fixture;
    public ConversationSessionConcurrencyTests(MySqlFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Same_session_serializes_while_different_stable_senders_lease_in_parallel()
    {
        var seeded = await SeedAsync(senderIsolated: true);
        var first = await IngestAsync(seeded.RobotId, "Alice", "stable-a", "first");
        var second = await IngestAsync(seeded.RobotId, "Alice renamed", "stable-a", "second");
        var other = await IngestAsync(seeded.RobotId, "Alice", "stable-b", "other");
        var now = DateTime.UtcNow;
        await using var db1 = Database();
        await using var db2 = Database();
        await using var db3 = Database();
        var repo1 = Repository(db1);
        var repo2 = Repository(db2);
        var repo3 = Repository(db3);

        var leasedFirst = await repo1.LeaseForProcessingAsync(first, "owner-1", now, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ConversationSessionBusyException>(() => repo2.LeaseForProcessingAsync(second, "owner-2", now, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        var leasedOther = await repo3.LeaseForProcessingAsync(other, "owner-3", now, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.NotEqual(leasedFirst.ConversationSessionId, leasedOther.ConversationSessionId);
        await repo1.PersistAnswerAndEnqueueAsync(leasedFirst, Result("first answer"), TestContext.Current.CancellationToken);
        await using var db4 = Database();
        var leasedSecond = await Repository(db4).LeaseForProcessingAsync(second, "owner-4", DateTime.UtcNow, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal(leasedFirst.ConversationSessionId, leasedSecond.ConversationSessionId);
        Assert.Equal(["first", "first answer"], leasedSecond.History.Select(message => message.Content));
        await repo3.ReleaseLeaseAsync(leasedOther.ConversationSessionId, "owner-3", TestContext.Current.CancellationToken);
        await Repository(db4).ReleaseLeaseAsync(leasedSecond.ConversationSessionId, "owner-4", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_stable_id_is_stateless_and_same_display_name_has_zero_prior_history()
    {
        var seeded = await SeedAsync(senderIsolated: true);
        var first = await IngestAsync(seeded.RobotId, "Same Name", null, "secret one");
        await using (var db = Database())
        {
            var repository = Repository(db);
            var leased = await repository.LeaseForProcessingAsync(first, "stateless-1", DateTime.UtcNow, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            await repository.PersistAnswerAndEnqueueAsync(leased, Result("answer one"), TestContext.Current.CancellationToken);
        }
        var second = await IngestAsync(seeded.RobotId, "Same Name", null, "secret two");
        await using var verify = Database();
        var next = await Repository(verify).LeaseForProcessingAsync(second, "stateless-2", DateTime.UtcNow, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.True(next.Scope.IsStatelessDegradation);
        Assert.Empty(next.History);
        await Repository(verify).ReleaseLeaseAsync(next.ConversationSessionId, "stateless-2", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Expired_session_lease_is_recoverable_by_another_owner()
    {
        var seeded = await SeedAsync(senderIsolated: false);
        var message = await IngestAsync(seeded.RobotId, "Alice", null, "recover");
        var now = DateTime.UtcNow;
        await using var firstDb = Database();
        var first = await Repository(firstDb).LeaseForProcessingAsync(message, "expired-owner", now, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await using var secondDb = Database();
        var recovered = await Repository(secondDb).LeaseForProcessingAsync(message, "new-owner", now.AddSeconds(2), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(first.ConversationSessionId, recovered.ConversationSessionId);
        Assert.Equal("new-owner", recovered.SessionLeaseOwner);
        await Repository(secondDb).ReleaseLeaseAsync(recovered.ConversationSessionId, "new-owner", TestContext.Current.CancellationToken);
    }

    private async Task<(Guid RobotId, Guid GroupId)> SeedAsync(bool senderIsolated)
    {
        await using var db = Database();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N");
        var robot = new RobotConfigEntity { Name = $"session-{suffix}", WorkToolRobotId = $"session-{suffix}", CallbackSecretHash = "test" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = $"Support-{suffix}", Name = $"Support-{suffix}", ContextSenderIsolated = senderIsolated };
        db.AddRange(robot, group, new ModelConfigEntity { Name = $"chat-{suffix}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.test", Model = "fake", EncryptedApiKey = "fake", IsDefault = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        currentGroup = group.Name;
        return (robot.Id, group.Id);
    }

    private string currentGroup = string.Empty;

    private async Task<Guid> IngestAsync(Guid robotId, string displayName, string? stableId, string text)
    {
        await using var db = Database();
        var repository = new DurableJobRepository(db);
        await repository.IngestInboundMessageAsync(new(robotId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), DateTime.UtcNow,
            currentGroup, displayName, text, DateTime.UtcNow, stableId), TestContext.Current.CancellationToken);
        return await db.ConversationMessages.Where(item => item.RobotConfigId == robotId && item.Text == text).Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);
    }

    private WechatRobotDbContext Database() => new(new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(fixture.ConnectionString).Options);
    private static GroundedConversationRepository Repository(WechatRobotDbContext db) => new(db, new ModelConfigurationService(new PassThroughProtector()), TimeProvider.System);
    private static GroundedAnswerResult Result(string text) => new(new(AnswerDecisionKind.Answer, text), new([], .7, .9, "policy", "Answer", InputSummaryJson: "{}"));

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }
}
