using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Identity;

namespace WechatRobot.IntegrationTests.Operations;

public sealed class AdministrationAuditEndpointTests : IClassFixture<UserAdministrationApiFactory>
{
    private readonly UserAdministrationApiFactory _factory;

    public AdministrationAuditEndpointTests(UserAdministrationApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Administration_audit_requires_admin_role()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/admin/administration-audits", TestContext.Current.CancellationToken)).StatusCode);

        using var knowledge = _factory.CreateClient();
        knowledge.DefaultRequestHeaders.Add("X-Test-Role", SystemRoles.KnowledgeOperator);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await knowledge.GetAsync("/api/admin/administration-audits", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await knowledge.GetAsync("/api/admin/administration-audits/filter-options", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Filter_options_are_distinct_bounded_and_keep_historical_targets()
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            "options-admin@example.test", "Options Admin", "Temporary1!Password", [SystemRoles.Admin]);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var at = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
            database.AdministrationAudits.AddRange(
                Enumerable.Range(0, 55).Select(index =>
                    Audit("user_created", "ApplicationUser", $"historical-user-{index:00}", at.AddMinutes(index), """{"email":"not-returned@example.test"}""")));
            database.AdministrationAudits.Add(
                Audit("model_configuration_created", "ModelConfig", "model-1", at.AddHours(2), "{}"));
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateAdminClient(admin);
        var response = await client.GetAsync(
            "/api/admin/administration-audits/filter-options?targetType=ApplicationUser&q=historical",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("not-returned@example.test", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(["audit-admin@example.test"], document.RootElement.GetProperty("actors").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(["model_configuration_created", "user_created"], document.RootElement.GetProperty("actions").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(["ApplicationUser", "ModelConfig"], document.RootElement.GetProperty("targetTypes").EnumerateArray().Select(item => item.GetString()!).ToArray());
        var targets = document.RootElement.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Equal(50, targets.Length);
        Assert.All(targets, target => Assert.Equal("ApplicationUser", target.GetProperty("targetType").GetString()));
        Assert.All(targets, target => Assert.Equal(
            ["label", "targetId", "targetType"],
            target.EnumerateObject().Select(property => property.Name).Order().ToArray()));
    }

    [Fact]
    public async Task Filters_use_inclusive_start_exclusive_end_and_return_only_defensively_sanitized_detail()
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            "audit-admin@example.test", "Audit Admin", "Temporary1!Password", [SystemRoles.Admin]);
        var start = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.AdministrationAudits.AddRange(
                Audit("user_created", "ApplicationUser", "inside-start", start,
                    """{"email":"safe@example.test","apiKey":"provider-secret","url":"https://files.test/a?token=secret"}"""),
                Audit("user_created", "ApplicationUser", "inside", start.AddHours(1), """{"enabled":true}"""),
                Audit("user_created", "ApplicationUser", "outside-end", start.AddDays(1), """{"enabled":false}"""),
                Audit("model_configuration_created", "ModelConfig", "other", start.AddHours(2), "{}"));
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateAdminClient(admin);
        var response = await client.GetAsync(
            "/api/admin/administration-audits?action=user_created&targetType=ApplicationUser" +
            $"&fromUtc={Uri.EscapeDataString(start.ToString("O"))}" +
            $"&toUtc={Uri.EscapeDataString(start.AddDays(1).ToString("O"))}&page=1&pageSize=20",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("provider-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("total").GetInt32());
        var ids = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("targetId").GetString())
            .ToArray();
        Assert.Contains("inside-start", ids);
        Assert.Contains("inside", ids);
        Assert.DoesNotContain("outside-end", ids);
    }

    [Fact]
    public async Task Rejects_an_invalid_utc_window()
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            "window-admin@example.test", "Window Admin", "Temporary1!Password", [SystemRoles.Admin]);
        using var client = _factory.CreateAdminClient(admin);

        var response = await client.GetAsync(
            "/api/admin/administration-audits?fromUtc=2026-07-25T00:00:00Z&toUtc=2026-07-24T00:00:00Z",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static AdministrationAuditEntity Audit(
        string action,
        string targetType,
        string targetId,
        DateTime createdAtUtc,
        string detail) => new()
        {
            Actor = "audit-admin@example.test",
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            SanitizedDetailJson = detail,
            CreatedAtUtc = createdAtUtc
        };
}
