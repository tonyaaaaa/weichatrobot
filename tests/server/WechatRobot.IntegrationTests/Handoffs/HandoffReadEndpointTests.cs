using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Models;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Handoffs;

public sealed class HandoffReadEndpointTests
{
    [Fact]
    public async Task Assignee_options_return_only_enabled_human_agents_and_admins()
    {
        await using var factory = new ReadApiFactory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var role in SystemRoles.All)
                if (!await roles.RoleExistsAsync(role))
                    Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);

            await CreateUserAsync(users, "agent@example.test", "客服甲", true, SystemRoles.HumanAgent);
            await CreateUserAsync(users, "admin@example.test", "管理员", true, SystemRoles.Admin);
            await CreateUserAsync(users, "disabled@example.test", "停用客服", false, SystemRoles.HumanAgent);
            await CreateUserAsync(users, "knowledge@example.test", "知识运营", true, SystemRoles.KnowledgeOperator);
        }

        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(
            "/api/handoffs/assignees",
            TestContext.Current.CancellationToken));
        var options = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(2, options.Length);
        Assert.Equal(["管理员", "客服甲"], options.Select(item => item.GetProperty("displayName").GetString()));
        Assert.All(options, item => Assert.True(item.GetProperty("isEnabled").GetBoolean()));
        Assert.All(options, item => Assert.Equal(
            ["displayName", "email", "id", "isEnabled", "roles"],
            item.EnumerateObject().Select(property => property.Name).Order().ToArray()));
    }

    private static async Task CreateUserAsync(
        UserManager<ApplicationUser> users,
        string email,
        string displayName,
        bool enabled,
        string role)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            DisplayName = displayName,
            IsEnabled = enabled
        };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
    }

    [Fact]
    public async Task Messages_and_transitions_are_paged_capped_and_stably_ordered()
    {
        await using var factory = new ReadApiFactory();
        Guid handoffId;
        Guid firstMessageId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var robot = new RobotConfigEntity { Name = "read", WorkToolRobotId = "read-robot", CallbackSecretHash = "hash" };
            var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = "read-group", Name = "读取群" };
            var question = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name,
                SenderDisplayName = "客户", Text = "问题", FallbackHash = Guid.NewGuid().ToString("N") };
            var handoff = new HandoffCaseEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, QuestionMessageId = question.Id,
                ReasonCode = "test", EvidenceJson = "{}", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
            var at = DateTime.UtcNow.AddMinutes(1);
            var messages = Enumerable.Range(0, 3).Select(index => new HandoffMessageEntity { HandoffCaseId = handoff.Id,
                SenderDisplayName = "agent", Text = $"m{index}", CreatedAtUtc = at.AddSeconds(index) }).ToArray();
            var transitions = Enumerable.Range(0, 3).Select(index => new HandoffTransitionEntity { HandoffCaseId = handoff.Id,
                Sequence = index + 1, FromState = "A", ToState = "B", ReasonCode = "test", IdempotencyKey = $"read-{index}",
                CreatedAtUtc = at.AddSeconds(index) }).ToArray();
            db.AddRange(robot, group, question, handoff); db.AddRange(messages); db.AddRange(transitions);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            handoffId = handoff.Id; firstMessageId = messages[0].Id;
        }

        using var client = factory.CreateClient();
        using var messagesJson = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/handoffs/{handoffId:D}/messages?page=1&pageSize=2", TestContext.Current.CancellationToken));
        Assert.Equal(3, messagesJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, messagesJson.RootElement.GetProperty("pageSize").GetInt32());
        var items = messagesJson.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(firstMessageId, items[0].GetProperty("id").GetGuid());

        using var transitionsJson = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/handoffs/{handoffId:D}/transitions?page=1&pageSize=500", TestContext.Current.CancellationToken));
        Assert.Equal(3, transitionsJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(100, transitionsJson.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(new[] { 1, 2, 3 }, transitionsJson.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("sequence").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Extreme_page_values_return_bad_request_for_every_task15_list()
    {
        await using var factory = new ReadApiFactory();
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var urls = new[]
        {
            "/api/handoffs/?page=2147483647&pageSize=100",
            $"/api/handoffs/{id:D}/messages?page=2147483647&pageSize=100",
            $"/api/handoffs/{id:D}/transitions?page=2147483647&pageSize=100",
            "/api/knowledge/candidates/?page=2147483647&pageSize=100"
        };
        foreach (var url in urls)
            Assert.Equal(System.Net.HttpStatusCode.BadRequest,
                (await client.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
    }

    public sealed class ReadApiFactory : WebApplicationFactory<Program>
    {
        private static readonly ServiceProvider InMemoryProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
        private readonly string _databaseName = $"handoff-read-{Guid.NewGuid():N}";

        public ReadApiFactory()
        {
            Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64",
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            Environment.SetEnvironmentVariable("Jwt__Issuer", "read-tests");
            Environment.SetEnvironmentVariable("Jwt__Audience", "read-tests-api");
            Environment.SetEnvironmentVariable("Jwt__SigningKey", "read-tests-signing-key-must-be-at-least-32-bytes");
            Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Database=unused");
            Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.DisableStartupMigrations();
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "read-tests", ["Jwt:Audience"] = "read-tests-api",
                ["Jwt:SigningKey"] = "read-tests-signing-key-must-be-at-least-32-bytes",
                ["ConnectionStrings:WechatRobot"] = "Server=localhost;Database=unused", ["Cors:AllowedOrigins:0"] = "https://admin.example.test"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<WechatRobotDbContext>>(); services.RemoveAll<WechatRobotDbContext>();
                services.AddDbContext<WechatRobotDbContext>(options => options.UseInMemoryDatabase(_databaseName).UseInternalServiceProvider(InMemoryProvider));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "integration-admin"; options.DefaultChallengeScheme = "integration-admin";
                        options.DefaultForbidScheme = "integration-admin";
                    })
                    .AddScheme<AuthenticationSchemeOptions, IntegrationAdminAuthenticationHandler>("integration-admin", _ => { });
            });
        }
    }
}
