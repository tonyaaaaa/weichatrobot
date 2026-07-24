using MySql.Data.MySqlClient;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.IntegrationTests.Infrastructure;

public sealed class MySqlFixtureIsolationTests
{
    [Fact]
    public async Task Logical_fixtures_use_isolated_databases_and_disposing_one_does_not_affect_the_other()
    {
        var first = new MySqlFixture();
        var second = new MySqlFixture();
        try
        {
            await first.InitializeAsync();
            await second.InitializeAsync();

            var firstBuilder = new MySqlConnectionStringBuilder(first.ConnectionString);
            var secondBuilder = new MySqlConnectionStringBuilder(second.ConnectionString);
            Assert.Equal(firstBuilder.Server, secondBuilder.Server);
            Assert.Equal(firstBuilder.Port, secondBuilder.Port);
            Assert.NotEqual(firstBuilder.Database, secondBuilder.Database);
            Assert.NotEqual(first.ConnectionString, second.ConnectionString);

            await using (var firstConnection = new MySqlConnection(first.ConnectionString))
            {
                await firstConnection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = new MySqlCommand("CREATE TABLE fixture_probe (Id int NOT NULL); INSERT INTO fixture_probe VALUES (1);", firstConnection);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using (var secondConnection = new MySqlConnection(second.ConnectionString))
            {
                await secondConnection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = new MySqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'fixture_probe'", secondConnection);
                Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
            }

            await first.DisposeAsync();
            await using var survivingConnection = new MySqlConnection(second.ConnectionString);
            await survivingConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using var survivingCommand = new MySqlCommand("SELECT DATABASE()", survivingConnection);
            Assert.Equal(secondBuilder.Database, Convert.ToString(await survivingCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Migrations_only_modify_the_initializing_fixture_database()
    {
        var migrating = new MySqlFixture();
        var untouched = new MySqlFixture();
        try
        {
            await migrating.InitializeAsync();
            await untouched.InitializeAsync();

            var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseMySQL(migrating.ConnectionString)
                .Options;
            await using (var database = new WechatRobotDbContext(options))
                await database.Database.MigrateAsync(TestContext.Current.CancellationToken);

            await using var untouchedConnection = new MySqlConnection(untouched.ConnectionString);
            await untouchedConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new MySqlCommand("""
                SELECT COUNT(*)
                FROM information_schema.tables
                WHERE table_schema = DATABASE() AND table_name = '__EFMigrationsHistory'
                """, untouchedConnection);
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }
        finally
        {
            await migrating.DisposeAsync();
            await untouched.DisposeAsync();
        }
    }
}
