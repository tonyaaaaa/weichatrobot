using Testcontainers.MySql;

namespace WechatRobot.IntegrationTests.Infrastructure;

public sealed class MySqlFixture : IAsyncLifetime
{
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.4.10")
        .WithDatabase("wechatrobot_tests")
        .WithUsername("wechatrobot")
        .WithPassword("wechatrobot-tests-password")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
