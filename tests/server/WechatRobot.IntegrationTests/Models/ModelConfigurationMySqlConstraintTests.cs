using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Models;

public sealed class ModelConfigurationMySqlConstraintTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Normalized_name_is_unique()
    {
        await using var database = CreateDatabase();
        await database.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);

        database.ModelConfigs.AddRange(
            Config("First", "SAME", "chat"),
            Config("Second", "SAME", "embedding"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => database.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Default_configuration_type_is_unique()
    {
        await using var database = CreateDatabase();
        await database.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);

        database.ModelConfigs.AddRange(
            DefaultConfig("chat", "one"),
            DefaultConfig("chat", "two"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => database.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Different_configuration_types_can_each_have_a_default()
    {
        await using var database = CreateDatabase();
        await database.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);

        database.ModelConfigs.AddRange(
            DefaultConfig("chat", "chat-default"),
            DefaultConfig("embedding", "embedding-default"));

        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await database.ModelConfigs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_default_attempts_leave_exactly_one_default()
    {
        await using var setup = CreateDatabase();
        await setup.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await setup.Database.ExecuteSqlRawAsync(
            "DELETE FROM `model_config`;",
            TestContext.Current.CancellationToken);

        var service = new ModelConfigurationService(new PassThroughProtector());
        var first = TestedConfig("concurrent-one", service);
        var second = TestedConfig("concurrent-two", service);
        setup.ModelConfigs.AddRange(first, second);
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var firstDatabase = CreateDatabase();
        await using var secondDatabase = CreateDatabase();
        var firstManager = new ModelConfigurationManager(
            firstDatabase, service, new RecordingChatCompletionClient(), new RecordingEmbeddingClient(), TimeProvider.System);
        var secondManager = new ModelConfigurationManager(
            secondDatabase, service, new RecordingChatCompletionClient(), new RecordingEmbeddingClient(), TimeProvider.System);

        var results = await Task.WhenAll(
            firstManager.SetDefaultAsync(first.Id, true, first.Version, "first", TestContext.Current.CancellationToken),
            secondManager.SetDefaultAsync(second.Id, true, second.Version, "second", TestContext.Current.CancellationToken));

        Assert.Contains(results, result => result.Status == ModelConfigurationMutationStatus.Success);
        setup.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await setup.ModelConfigs.CountAsync(
                item => item.ConfigurationType == "chat" && item.IsDefault,
                TestContext.Current.CancellationToken));
    }

    private WechatRobotDbContext CreateDatabase() => new(
        new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options);

    private static ModelConfigEntity Config(string name, string normalizedName, string type) => new()
    {
        Name = name,
        NormalizedName = normalizedName,
        Provider = "fake",
        ConfigurationType = type,
        BaseUrl = "https://fake.test",
        Model = "fake"
    };

    private static ModelConfigEntity DefaultConfig(string type, string name)
    {
        var entity = Config(name, name.ToUpperInvariant(), type);
        entity.IsDefault = true;
        return entity;
    }

    private static ModelConfigEntity TestedConfig(string name, ModelConfigurationService service)
    {
        var entity = Config(name, name.ToUpperInvariant(), "chat");
        entity.ConnectionStatus = ModelConnectionStatus.Succeeded;
        entity.TestedConfigurationFingerprint = service.ComputeFingerprint(
            new ModelConfigurationRecord(
                entity.Id, entity.Name, entity.Provider, entity.BaseUrl, entity.Model, entity.EncryptedApiKey,
                entity.TimeoutSeconds, entity.MaxRetries, entity.IsEnabled, entity.IsDefault),
            entity.ConfigurationType,
            entity.ApiKeyVersion);
        return entity;
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
