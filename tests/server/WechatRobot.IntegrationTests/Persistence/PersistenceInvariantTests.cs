using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Persistence;

public sealed class PersistenceInvariantTests
{
    [Fact]
    public async Task Save_rejects_an_enabled_archived_group()
    {
        await using var database = CreateDatabase();
        database.GroupProfiles.Add(new GroupProfileEntity
        {
            Name = "archived-but-enabled",
            IsEnabled = true,
            ArchivedAtUtc = DateTime.UtcNow
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains(nameof(GroupProfileEntity.ArchivedAtUtc), exception.Message);
    }

    [Fact]
    public async Task Save_rejects_robot_send_rate_outside_supported_range()
    {
        await using var database = CreateDatabase();
        database.RobotConfigs.Add(new RobotConfigEntity
        {
            Name = "invalid-rate",
            WorkToolRobotId = "invalid-rate",
            CallbackSecretHash = "test",
            SendRateLimitPerMinute = 61
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains(nameof(RobotConfigEntity.SendRateLimitPerMinute), exception.Message);
    }

    [Fact]
    public async Task Save_rejects_unknown_handoff_pause_policy()
    {
        await using var database = CreateDatabase();
        database.GroupProfiles.Add(new GroupProfileEntity
        {
            Name = "invalid-policy",
            HandoffPausePolicy = "Unknown"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains(nameof(GroupProfileEntity.HandoffPausePolicy), exception.Message);
    }

    [Fact]
    public async Task Save_rejects_unknown_group_registration_source()
    {
        await using var database = CreateDatabase();
        database.GroupProfiles.Add(new GroupProfileEntity
        {
            Name = "invalid-source",
            RegistrationSource = "Unknown"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains(nameof(GroupProfileEntity.RegistrationSource), exception.Message);
    }

    [Fact]
    public void Save_rejects_unknown_human_agent_verification_status()
    {
        using var database = CreateDatabase();
        database.GroupHumanAgents.Add(new GroupHumanAgentEntity
        {
            GroupProfileId = Guid.NewGuid(),
            ApplicationUserId = Guid.NewGuid(),
            WorkToolDisplayNameSnapshot = "Agent",
            VerificationStatus = "Unknown"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => database.SaveChanges());

        Assert.Contains(nameof(GroupHumanAgentEntity.VerificationStatus), exception.Message);
    }

    private static WechatRobotDbContext CreateDatabase() => new(
        new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase($"persistence-invariants-{Guid.NewGuid():N}")
            .Options);
}
