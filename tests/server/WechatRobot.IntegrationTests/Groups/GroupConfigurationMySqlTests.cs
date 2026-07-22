using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.IntegrationTests.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Groups;

public sealed class GroupConfigurationMySqlTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Configuration_binds_multiple_tags_on_mysql()
    {
        await using var factory = new MySqlGroupApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity { Name = "mysql-group", WorkToolRobotId = Guid.NewGuid().ToString("N"), CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = Guid.NewGuid().ToString("N"), Name = "技术部" };
        var product = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
        var support = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
        database.AddRange(robot, group, product, support);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/groups/{group.Id}/configuration", new
        {
            includeRules = Array.Empty<object>(), excludeRules = Array.Empty<object>(), boundTagIds = new[] { product.Id, support.Id },
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null, tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null },
            clearContext = false
        }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        database.ChangeTracker.Clear();
        Assert.Equal(2, await database.GroupProfileTags.CountAsync(binding => binding.GroupProfileId == group.Id, TestContext.Current.CancellationToken));
    }

    private sealed class MySqlGroupApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        public MySqlGroupApiFactory(string connectionString)
        {
            _connectionString = connectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", connectionString);
            Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
            Environment.SetEnvironmentVariable("Jwt__Issuer", "mysql-group-tests");
            Environment.SetEnvironmentVariable("Jwt__Audience", "mysql-group-tests-api");
            Environment.SetEnvironmentVariable("Jwt__SigningKey", "mysql-group-tests-signing-key-at-least-32-bytes");
            Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WechatRobot"] = _connectionString,
                ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
                ["Jwt:Issuer"] = "mysql-group-tests",
                ["Jwt:Audience"] = "mysql-group-tests-api",
                ["Jwt:SigningKey"] = "mysql-group-tests-signing-key-at-least-32-bytes"
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(service => service.ServiceType == typeof(DbContextOptions<WechatRobotDbContext>)).ToArray()) services.Remove(descriptor);
                services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "integration-admin";
                        options.DefaultChallengeScheme = "integration-admin";
                        options.DefaultForbidScheme = "integration-admin";
                    })
                    .AddScheme<AuthenticationSchemeOptions, IntegrationAdminAuthenticationHandler>("integration-admin", _ => { });
            });
        }
    }
}
