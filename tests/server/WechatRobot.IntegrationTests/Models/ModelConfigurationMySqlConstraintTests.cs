using Microsoft.EntityFrameworkCore;
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
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);

        database.ModelConfigs.AddRange(
            DefaultConfig("chat", "chat-default"),
            DefaultConfig("embedding", "embedding-default"));

        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await database.ModelConfigs.CountAsync(TestContext.Current.CancellationToken));
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
}
