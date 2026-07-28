using MySql.Data.MySqlClient;
using System.Text;
using Testcontainers.MySql;

namespace WechatRobot.IntegrationTests.Infrastructure;

public sealed class MySqlFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim ServerGate = new(1, 1);
    private static readonly MySqlContainer SharedContainer = new MySqlBuilder(ResolveImage())
        .WithDatabase("wechatrobot_fixture_host")
        .WithUsername("wechatrobot")
        .WithPassword("wechatrobot-tests-password")
        .WithCommand(
            "--character-set-server=utf8mb4",
            "--collation-server=utf8mb4_bin",
            "--log-bin-trust-function-creators=1")
        // The database is disposable test state. Keeping it in the Linux VM's tmpfs avoids the
        // exceptionally slow Docker Desktop bind-layer fsync/ALTER path on Windows.
        .WithTmpfsMount("/var/lib/mysql")
        .WithResourceMapping(Encoding.UTF8.GetBytes(
            "GRANT ALL PRIVILEGES ON `wechatrobot\\_it\\_%`.* TO 'wechatrobot'@'%';\n"),
            "/docker-entrypoint-initdb.d/001-grant-fixture-databases.sql")
        .Build();
    private static bool _serverStarted;
    private string? _databaseName;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await ServerGate.WaitAsync();
        try
        {
            if (!_serverStarted)
            {
                try
                {
                    await SharedContainer.StartAsync();
                    _serverStarted = true;
                }
                catch
                {
                    _serverStarted = false;
                    throw;
                }
            }

            var databaseName = $"wechatrobot_it_{Guid.NewGuid():N}";
            var builder = new MySqlConnectionStringBuilder(SharedContainer.GetConnectionString());
            builder.Database = databaseName;
            var connectionString = builder.ConnectionString;

            builder.Database = "wechatrobot_fixture_host";
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            try
            {
                await using var command = new MySqlCommand($"CREATE DATABASE `{databaseName}`", connection);
                await command.ExecuteNonQueryAsync();
                _databaseName = databaseName;
                ConnectionString = connectionString;
            }
            catch
            {
                await using var cleanup = new MySqlCommand($"DROP DATABASE IF EXISTS `{databaseName}`", connection);
                await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
                _databaseName = null;
                ConnectionString = string.Empty;
                throw;
            }
        }
        finally
        {
            ServerGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_databaseName)) return;

        await ServerGate.WaitAsync();
        try
        {
            var databaseName = _databaseName;
            try
            {
                // Clear only this fixture's pool so no pooled session keeps the soon-to-be-dropped database selected.
                await using var pooledConnection = new MySqlConnection(ConnectionString);
                await pooledConnection.ClearPoolAsync(pooledConnection, CancellationToken.None);

                var builder = new MySqlConnectionStringBuilder(SharedContainer.GetConnectionString()) { Database = "wechatrobot_fixture_host" };
                await using var connection = new MySqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using var command = new MySqlCommand($"DROP DATABASE IF EXISTS `{databaseName}`", connection);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                _databaseName = null;
                ConnectionString = string.Empty;
            }
        }
        finally
        {
            ServerGate.Release();
        }
    }

    // The process-wide container is intentionally not disposed per fixture. Testcontainers' Ryuk resource
    // reaper owns process-level cleanup; each logical fixture only creates and drops its isolated database.

    private static string ResolveImage()
    {
        var configured = Environment.GetEnvironmentVariable("WECHATROBOT_TEST_MYSQL_IMAGE");
        return string.IsNullOrWhiteSpace(configured) ? "mysql:8.4.10" : configured.Trim();
    }
}
