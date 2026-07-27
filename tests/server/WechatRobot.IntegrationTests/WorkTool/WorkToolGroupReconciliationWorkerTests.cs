using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Infrastructure.WorkTool;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class WorkToolGroupReconciliationWorkerTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;

    public WorkToolGroupReconciliationWorkerTests(MySqlFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Successful_create_with_one_exact_remote_match_creates_one_local_imported_group()
    {
        var groupName = $"新建客服群-{Guid.NewGuid():N}";
        var client = new GroupListClient([groupName]);
        using var provider = Services(client);
        var auditId = await SeedAsync(
            provider,
            WorkToolGroupOperationKind.Create,
            groupName,
            null,
            WorkToolCommandStatuses.ExecutedSucceeded,
            createLocalGroup: false);
        var worker = new WorkToolGroupReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var audit = await database.WorkToolOperationAudits.AsNoTracking()
            .SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        var group = await database.GroupProfiles.AsNoTracking()
            .SingleAsync(item => item.Name == groupName, TestContext.Current.CancellationToken);
        Assert.Equal("Reconciled", audit.ReconciliationStatus);
        Assert.Equal(group.Id, audit.ReconciledGroupProfileId);
        Assert.Equal("WorkToolImport", group.RegistrationSource);
    }

    [Fact]
    public async Task Successful_rename_with_one_exact_remote_match_updates_the_single_local_group()
    {
        var originalName = $"旧群-{Guid.NewGuid():N}";
        var targetName = $"新群-{Guid.NewGuid():N}";
        var client = new GroupListClient([targetName]);
        using var provider = Services(client);
        var auditId = await SeedAsync(
            provider,
            WorkToolGroupOperationKind.Rename,
            originalName,
            targetName,
            WorkToolCommandStatuses.ExecutedSucceeded,
            createLocalGroup: true);
        var worker = new WorkToolGroupReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var audit = await database.WorkToolOperationAudits.AsNoTracking()
            .SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        var group = await database.GroupProfiles.AsNoTracking()
            .SingleAsync(item => item.Name == targetName, TestContext.Current.CancellationToken);
        Assert.Equal("Reconciled", audit.ReconciliationStatus);
        Assert.Equal(group.Id, audit.ReconciledGroupProfileId);
        Assert.Equal(1, group.ConfigurationVersion);
    }

    [Fact]
    public async Task Zero_or_multiple_exact_remote_matches_requires_confirmation()
    {
        var name = $"歧义群-{Guid.NewGuid():N}";
        var client = new GroupListClient([name, name]);
        using var provider = Services(client);
        var auditId = await SeedAsync(
            provider,
            WorkToolGroupOperationKind.Create,
            name,
            null,
            WorkToolCommandStatuses.ExecutedSucceeded,
            createLocalGroup: false);
        var worker = new WorkToolGroupReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var audit = await database.WorkToolOperationAudits.AsNoTracking()
            .SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal("NeedsConfirmation", audit.ReconciliationStatus);
        Assert.False(await database.GroupProfiles.AsNoTracking()
            .AnyAsync(item => item.Name == name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_worktool_result_is_never_reconciled()
    {
        var name = $"失败群-{Guid.NewGuid():N}";
        var client = new GroupListClient([name]);
        using var provider = Services(client);
        var auditId = await SeedAsync(
            provider,
            WorkToolGroupOperationKind.Create,
            name,
            null,
            WorkToolCommandStatuses.ExecutedFailed,
            createLocalGroup: false);
        var worker = new WorkToolGroupReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.False(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var audit = await database.WorkToolOperationAudits.AsNoTracking()
            .SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Null(audit.ReconciliationStatus);
        Assert.Equal(0, client.ListCalls);
    }

    [Fact]
    public async Task Transient_group_list_failure_schedules_bounded_retry_without_renaming_local_group()
    {
        var originalName = $"保留旧群-{Guid.NewGuid():N}";
        var targetName = $"暂不可见-{Guid.NewGuid():N}";
        var client = new GroupListClient([])
        {
            Failure = new WorkToolGroupListException("worktool_group_list_unavailable")
        };
        using var provider = Services(client);
        var auditId = await SeedAsync(
            provider,
            WorkToolGroupOperationKind.Rename,
            originalName,
            targetName,
            WorkToolCommandStatuses.ExecutedSucceeded,
            createLocalGroup: true);
        var worker = new WorkToolGroupReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var audit = await database.WorkToolOperationAudits.AsNoTracking()
            .SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal("Retrying", audit.ReconciliationStatus);
        Assert.NotNull(audit.ReconciliationNextAttemptAtUtc);
        Assert.True(audit.ReconciliationAttemptCount <= 5);
        Assert.True(await database.GroupProfiles.AsNoTracking()
            .AnyAsync(item => item.Name == originalName, TestContext.Current.CancellationToken));
        Assert.False(await database.GroupProfiles.AsNoTracking()
            .AnyAsync(item => item.Name == targetName, TestContext.Current.CancellationToken));
    }

    private ServiceProvider Services(GroupListClient client) => new ServiceCollection()
        .AddDbContextFactory<WechatRobotDbContext>(
            options => options.UseMySQL(_fixture.ConnectionString))
        .AddSingleton<ISecretProtector, PassThroughProtector>()
        .AddSingleton<IWorkToolClient>(client)
        .AddSingleton(TimeProvider.System)
        .AddScoped<WorkToolGroupImportService>()
        .BuildServiceProvider();

    private static async Task<Guid> SeedAsync(
        ServiceProvider provider,
        WorkToolGroupOperationKind kind,
        string groupName,
        string? value,
        string resultStatus,
        bool createLocalGroup)
    {
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity
        {
            Name = Guid.NewGuid().ToString("N"),
            WorkToolRobotId = Guid.NewGuid().ToString("N"),
            CallbackSecretHash = "test"
        };
        var command = new WorkToolGroupOperationRequest(
            robot.Id,
            kind,
            groupName,
            ["成员甲"],
            value);
        var audit = new WorkToolOperationAuditEntity
        {
            RobotConfigId = robot.Id,
            OperatorName = "admin",
            Operation = kind.ToString(),
            WorkToolCommandNumber = kind == WorkToolGroupOperationKind.Create ? 206 : 207,
            SanitizedRequestJson = "{}",
            Status = resultStatus,
            EncryptedCommandJson = JsonSerializer.Serialize(command),
            ReconciliationStatus = resultStatus == WorkToolCommandStatuses.ExecutedSucceeded
                ? "Pending"
                : null
        };
        database.AddRange(robot, audit);
        if (createLocalGroup)
        {
            database.GroupProfiles.Add(new GroupProfileEntity
            {
                RobotConfigId = robot.Id,
                Name = groupName,
                WorkToolGroupRemark = groupName
            });
        }
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return audit.Id;
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class GroupListClient(IReadOnlyList<string> groupNames) : IWorkToolClient
    {
        public Exception? Failure { get; init; }
        public int ListCalls { get; private set; }

        public Task<WorkToolGroupPage> ListGroupsAsync(
            Guid robotConfigId,
            string? requestedName,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            ListCalls++;
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(new WorkToolGroupPage(
                1,
                pageSize,
                1,
                groupNames.Count,
                groupNames.Select(name =>
                    new WorkToolGroupSummary(name, "群主", 2, null)).ToArray()));
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
