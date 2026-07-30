using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Identity;

namespace WechatRobot.IntegrationTests.FixedReplies;

public sealed class FixedReplyTemplateEndpointTests
    : IClassFixture<UserAdministrationApiFactory>
{
    private readonly UserAdministrationApiFactory factory;

    public FixedReplyTemplateEndpointTests(UserAdministrationApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Admin_can_create_list_update_and_audit_global_template()
    {
        await factory.ResetAsync();
        var admin = await factory.CreateUserAsync(
            "fixed-reply-admin@example.test",
            "Fixed Reply Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        using var client = factory.CreateAdminClient(admin);

        var createdResponse = await client.PostAsJsonAsync(
            "/api/admin/fixed-reply-templates",
            new
            {
                name = "签证进度",
                intentDescription = "询问已提交签证的出签进度",
                replyText = "请以顾问最新通知为准。",
                scopeType = "Global",
                priority = 100,
                isEnabled = true,
                examples = new[] { "签证还有多久出来？" },
                groupRules = Array.Empty<object>()
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<TemplateResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        var list = await client.GetFromJsonAsync<TemplateResponse[]>(
            "/api/admin/fixed-reply-templates",
            TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, Assert.Single(list!).Id);

        var conflict = await client.PutAsJsonAsync(
            $"/api/admin/fixed-reply-templates/{created.Id}",
            new
            {
                expectedVersion = 99,
                name = "签证进度",
                intentDescription = "询问已提交签证的出签进度",
                replyText = "请以顾问最新通知为准。",
                scopeType = "Global",
                priority = 100,
                isEnabled = true,
                examples = new[] { "签证还有多久出来？" },
                groupRules = Array.Empty<object>()
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var scope = factory.Services.CreateScope();
        var audits = await scope.ServiceProvider
            .GetRequiredService<WechatRobotDbContext>()
            .AdministrationAudits
            .Where(item => item.TargetId == created.Id.ToString("D"))
            .Select(item => item.Action)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Contains("fixed_reply_template.created", audits);
    }

    [Fact]
    public async Task Global_template_accepts_excluded_group_effect_as_json_string()
    {
        await factory.ResetAsync();
        var admin = await factory.CreateUserAsync(
            "fixed-reply-exclusion-admin@example.test",
            "Fixed Reply Exclusion Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        Guid groupId;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider
                .GetRequiredService<WechatRobotDbContext>();
            var robot = new RobotConfigEntity
            {
                Name = "fixed-reply-exclusion-robot",
                WorkToolRobotId = "fixed-reply-exclusion-robot",
                CallbackSecretHash = "hash"
            };
            var group = new GroupProfileEntity
            {
                RobotConfigId = robot.Id,
                Name = "固定回复排除群"
            };
            database.AddRange(robot, group);
            await database.SaveChangesAsync(
                TestContext.Current.CancellationToken);
            groupId = group.Id;
        }
        using var client = factory.CreateAdminClient(admin);

        var response = await client.PostAsJsonAsync(
            "/api/admin/fixed-reply-templates",
            new
            {
                name = "询问签证进度",
                intentDescription = "询问签证进度",
                replyText = "您好，签证进度会实时更新，如果出签会第一时间通知您的",
                scopeType = "Global",
                priority = 0,
                isEnabled = true,
                examples = new[] { "签证还有多久出", "签证结果出了吗" },
                groupRules = new[]
                {
                    new
                    {
                        groupProfileId = groupId,
                        effect = "Exclude"
                    }
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "Global",
            json.RootElement.GetProperty("scopeType").GetString());
        Assert.Equal(
            "Exclude",
            json.RootElement
                .GetProperty("groupRules")[0]
                .GetProperty("effect")
                .GetString());
    }

    [Fact]
    public async Task Group_filter_keeps_global_templates_without_explicit_group_rules()
    {
        await factory.ResetAsync();
        var admin = await factory.CreateUserAsync(
            "fixed-reply-group-filter-admin@example.test",
            "Fixed Reply Group Filter Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        Guid groupId;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider
                .GetRequiredService<WechatRobotDbContext>();
            var robot = new RobotConfigEntity
            {
                Name = "fixed-reply-group-filter-robot",
                WorkToolRobotId = "fixed-reply-group-filter-robot",
                CallbackSecretHash = "hash"
            };
            var group = new GroupProfileEntity
            {
                RobotConfigId = robot.Id,
                Name = "固定回复筛选群"
            };
            database.AddRange(robot, group);
            await database.SaveChangesAsync(
                TestContext.Current.CancellationToken);
            groupId = group.Id;
        }
        using var client = factory.CreateAdminClient(admin);
        var createResponse = await client.PostAsJsonAsync(
            "/api/admin/fixed-reply-templates",
            new
            {
                name = "全局无显式规则模板",
                intentDescription = "验证群筛选仍返回全局模板",
                replyText = "全局固定回复。",
                scopeType = "Global",
                priority = 0,
                isEnabled = true,
                examples = new[] { "全局模板测试" },
                groupRules = Array.Empty<object>()
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<TemplateResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        var filtered = await client.GetFromJsonAsync<TemplateResponse[]>(
            $"/api/admin/fixed-reply-templates?groupProfileId={groupId:D}",
            TestContext.Current.CancellationToken);

        Assert.Contains(filtered!, item => item.Id == created.Id);
    }

    private sealed record TemplateResponse(Guid Id, int Version);
}
