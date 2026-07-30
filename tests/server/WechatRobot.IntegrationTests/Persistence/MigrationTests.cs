using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Persistence;

public sealed class MigrationTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;

    public MigrationTests(MySqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Apply_migrations_and_seed_roles_idempotently()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_fixture.ConnectionString));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<WechatRobotDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        await IdentitySeeder.SeedRolesAsync(roleManager, TestContext.Current.CancellationToken);
        await IdentitySeeder.SeedRolesAsync(roleManager, TestContext.Current.CancellationToken);

        var roles = await context.Roles.OrderBy(role => role.Name).Select(role => role.Name).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { SystemRoles.Admin, SystemRoles.HumanAgent, SystemRoles.KnowledgeOperator }, roles);
    }

    [Fact]
    public async Task Existing_model_configuration_receives_provider_field_defaults_when_migrated()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(_fixture.ConnectionString)
            .Options;
        await using var context = new WechatRobotDbContext(options);
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.MigrateAsync("20260721092312_InitialIdentityMessaging", TestContext.Current.CancellationToken);

        var modelConfigId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO model_config (Id, Name, Provider, Model, IsEnabled, IsDefault, CreatedAtUtc, UpdatedAtUtc)
            VALUES ({modelConfigId}, {"existing-chat"}, {"openai-compatible"}, {"gpt-existing"}, {true}, {false}, {timestamp}, {timestamp})
            """, TestContext.Current.CancellationToken);

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var migrated = await context.ModelConfigs.SingleAsync(config => config.Id == modelConfigId, TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, migrated.BaseUrl);
        Assert.Equal(string.Empty, migrated.ConfigurationType);
        Assert.Null(migrated.EncryptedApiKey);
        Assert.Equal(30, migrated.TimeoutSeconds);
        Assert.Equal(0, migrated.MaxRetries);
        Assert.Equal("EXISTING-CHAT", migrated.NormalizedName);
        Assert.Equal(ModelConnectionStatus.Untested, migrated.ConnectionStatus);
        Assert.Null(migrated.LastTestedAtUtc);
        Assert.Null(migrated.LastTestFailureSummary);
        Assert.Null(migrated.TestedConfigurationFingerprint);
        Assert.Equal(0, migrated.ApiKeyVersion);
        Assert.Equal(0, migrated.Version);
    }

    [Fact]
    public async Task Existing_group_receives_safe_defaults_without_changing_configuration_version()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).Options;
        await using var context = new WechatRobotDbContext(options);
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await context.Database.MigrateAsync("20260723044912_AddWorkerHeartbeat", TestContext.Current.CancellationToken);
        var robotId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO robot_config (Id, Name, WorkToolRobotId, CallbackSecretHash, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
            VALUES ({robotId}, {"migration-policy-robot"}, {Guid.NewGuid().ToString("N")}, {"hash"}, {true}, {timestamp}, {timestamp});
            INSERT INTO group_profile (Id, RobotConfigId, ExternalGroupId, Name, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
            VALUES ({groupId}, {robotId}, {Guid.NewGuid().ToString("N")}, {"migration-policy-group"}, {true}, {timestamp}, {timestamp});
            """, TestContext.Current.CancellationToken);

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var group = await context.GroupProfiles.AsNoTracking().SingleAsync(item => item.Id == groupId, TestContext.Current.CancellationToken);
        Assert.Equal("Group", group.HandoffPausePolicy);
        Assert.Equal(0, group.ConfigurationVersion);
        Assert.Null(group.ArchivedAtUtc);
        Assert.Equal(0, group.StateVersion);
    }
}
