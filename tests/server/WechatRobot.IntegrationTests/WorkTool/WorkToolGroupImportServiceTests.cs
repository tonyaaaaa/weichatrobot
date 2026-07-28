using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Infrastructure.WorkTool;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class WorkToolGroupImportServiceTests(MySqlFixture fixture)
    : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Importing_a_unique_archived_match_restores_the_same_disabled_record()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<WechatRobotDbContext>(
            options => options.UseMySQL(fixture.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<WechatRobotDbContext>>();
        var robot = new RobotConfigEntity
        {
            Name = $"restore-import-{Guid.NewGuid():N}",
            WorkToolRobotId = $"restore-import-{Guid.NewGuid():N}",
            CallbackSecretHash = "test"
        };
        var archived = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = "归档测试群",
            WorkToolGroupRemark = "归档测试群",
            IsEnabled = false,
            ArchivedAtUtc = DateTime.UtcNow.AddDays(-1),
            StateVersion = 4
        };
        await using (var database = await factory.CreateDbContextAsync(
                         TestContext.Current.CancellationToken))
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            database.AddRange(robot, archived);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var client = new FixedGroupClient(new(
            1, 100, 1, 1, [new("归档测试群", "群主", 5, null)]));
        var service = new WorkToolGroupImportService(factory, client, TimeProvider.System);

        var result = await service.ImportAsync(
            robot.Id,
            [new("归档测试群", "Available")],
            "admin@test",
            TestContext.Current.CancellationToken);

        Assert.Equal(archived.Id, Assert.Single(result).GroupProfileId);
        await using var verify = await factory.CreateDbContextAsync(
            TestContext.Current.CancellationToken);
        var restored = await verify.GroupProfiles.SingleAsync(
            group => group.Id == archived.Id,
            TestContext.Current.CancellationToken);
        Assert.Null(restored.ArchivedAtUtc);
        Assert.False(restored.IsEnabled);
        Assert.Equal(5, restored.StateVersion);
        Assert.Contains(
            await verify.AdministrationAudits.Where(audit => audit.TargetId == archived.Id.ToString("D"))
                .ToArrayAsync(TestContext.Current.CancellationToken),
            audit => audit.Action == "worktool_group_restored");
    }

    [Fact]
    public async Task ImportAsync_creates_only_selected_remote_groups_and_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<WechatRobotDbContext>(
            options => options.UseMySQL(fixture.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<WechatRobotDbContext>>();
        var robotId = Guid.NewGuid();
        await using (var database = await factory.CreateDbContextAsync(
                         TestContext.Current.CancellationToken))
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            database.RobotConfigs.Add(new RobotConfigEntity
            {
                Id = robotId,
                Name = "import-test",
                WorkToolRobotId = $"legacy-{Guid.NewGuid():N}",
                CallbackSecretHash = "test",
                IsEnabled = true
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = new FixedGroupClient(
            new WorkToolGroupPage(
                1,
                100,
                1,
                2,
                [
                    new("已选择群", "群主甲", 12, "公告"),
                    new("未选择群", "群主乙", 5, null)
                ]));
        var service = new WorkToolGroupImportService(
            factory,
            client,
            TimeProvider.System);

        var first = await service.ImportAsync(
            robotId,
            [new GroupImportSelection("已选择群", "Available")],
            "admin@test",
            TestContext.Current.CancellationToken);
        var second = await service.ImportAsync(
            robotId,
            [new GroupImportSelection("已选择群", "Available")],
            "admin@test",
            TestContext.Current.CancellationToken);

        Assert.Equal("Imported", Assert.Single(first).Status);
        Assert.Equal(Assert.Single(first).GroupProfileId, Assert.Single(second).GroupProfileId);
        await using var verify = await factory.CreateDbContextAsync(
            TestContext.Current.CancellationToken);
        var groups = await verify.GroupProfiles
            .Where(group => group.RobotConfigId == robotId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var imported = Assert.Single(groups);
        Assert.Equal("已选择群", imported.Name);
        Assert.Equal("WorkToolImport", imported.RegistrationSource);
        Assert.NotNull(imported.WorkToolImportedAtUtc);
        Assert.DoesNotContain(groups, group => group.Name == "未选择群");
        Assert.Contains(
            await verify.AdministrationAudits
                .Where(audit => audit.TargetId == imported.Id.ToString("D"))
                .ToArrayAsync(TestContext.Current.CancellationToken),
            audit => audit.Action == "worktool_group_imported"
                && !audit.SanitizedDetailJson.Contains("legacy-", StringComparison.Ordinal));
    }

    private sealed class FixedGroupClient(WorkToolGroupPage page) : IWorkToolClient
    {
        public Task<WorkToolGroupPage> ListGroupsAsync(
            Guid robotConfigId,
            string? groupName,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var items = string.IsNullOrWhiteSpace(groupName)
                ? page.Items
                : page.Items.Where(item => item.GroupName == groupName.Trim()).ToArray();
            return Task.FromResult(page with { Items = items, Total = items.Count });
        }

        public Task<WorkToolCommandSubmission> SendTextAsync(
            WorkToolSendRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkToolCommandSubmission> ExecuteGroupOperationAsync(
            WorkToolGroupOperationRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkToolSendResult> TestConnectionAsync(
            Guid robotConfigId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkToolSendResult> BindCallbackAsync(
            Guid robotConfigId,
            int type,
            Uri callbackUrl,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
