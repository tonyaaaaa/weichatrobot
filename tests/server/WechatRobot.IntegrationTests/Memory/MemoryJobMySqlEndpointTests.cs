using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.IntegrationTests.Models;

namespace WechatRobot.IntegrationTests.Memory;

public sealed class MemoryJobMySqlEndpointTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Job_list_filters_supported_memory_jobs_on_mysql()
    {
        await using var factory = new MemoryJobApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        var robot = new RobotConfigEntity
        {
            Name = $"memory-job-{Guid.NewGuid():N}",
            WorkToolRobotId = $"memory-job-robot-{Guid.NewGuid():N}",
            CallbackSecretHash = "hash"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = $"记忆任务群-{Guid.NewGuid():N}"
        };
        var memoryJob = new DurableJobEntity
        {
            JobType = "ExtractConversationMemory",
            GroupProfileId = group.Id,
            PayloadJson = "{}",
            Status = "pending",
            AvailableAtUtc = now,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var unrelatedJob = new DurableJobEntity
        {
            JobType = "SendMessage",
            GroupProfileId = group.Id,
            PayloadJson = "{}",
            Status = "pending",
            AvailableAtUtc = now,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.AddRange(robot, group, memoryJob, unrelatedJob);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/api/admin/memory/jobs?groupProfileId={group.Id:D}&page=1&pageSize=20",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(
            "ExtractConversationMemory",
            body.RootElement.GetProperty("items")[0].GetProperty("jobType").GetString());
    }

    private sealed class MemoryJobApiFactory : WebApplicationFactory<Program>
    {
        private readonly string connectionString;

        public MemoryJobApiFactory(string connectionString)
        {
            this.connectionString = connectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", connectionString);
            Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
            Environment.SetEnvironmentVariable("Jwt__Issuer", "memory-job-tests");
            Environment.SetEnvironmentVariable("Jwt__Audience", "memory-job-tests-api");
            Environment.SetEnvironmentVariable(
                "Jwt__SigningKey",
                "memory-job-tests-signing-key-at-least-32-bytes");
            Environment.SetEnvironmentVariable(
                "WECHATROBOT_MASTER_KEY_BASE64",
                Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.DisableStartupMigrations();
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:WechatRobot"] = connectionString,
                    ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
                    ["Jwt:Issuer"] = "memory-job-tests",
                    ["Jwt:Audience"] = "memory-job-tests-api",
                    ["Jwt:SigningKey"] = "memory-job-tests-signing-key-at-least-32-bytes"
                }));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services
                    .Where(service => service.ServiceType == typeof(DbContextOptions<WechatRobotDbContext>))
                    .ToArray())
                    services.Remove(descriptor);
                services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "integration-admin";
                        options.DefaultChallengeScheme = "integration-admin";
                        options.DefaultForbidScheme = "integration-admin";
                    })
                    .AddScheme<AuthenticationSchemeOptions, IntegrationAdminAuthenticationHandler>(
                        "integration-admin",
                        _ => { });
            });
        }
    }
}
