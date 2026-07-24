using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class GroupOperationWorkerTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;
    public GroupOperationWorkerTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Queued_operation_is_dispatched_once_and_marked_accepted()
    {
        var client = new RecordingClient();
        using var provider = Services(client);
        var auditId = await SeedAsync(provider, WorkToolCommandStatuses.Queued, null);

        var worker = new WorkToolGroupOperationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .WorkToolOperationAudits.AsNoTracking().SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.Accepted, stored.Status);
        Assert.Equal("fake-group-command", stored.WorkToolCommandMessageId);
        Assert.NotEqual(WorkToolCommandStatuses.ExecutedSucceeded, stored.Status);
        Assert.Null(stored.CompletedAtUtc);
        Assert.Equal(1, client.Calls);
        Assert.NotNull(stored.ExternalDispatchStartedAtUtc);
    }

    [Fact]
    public async Task Expired_external_operation_becomes_uncertain_without_redispatch()
    {
        var client = new RecordingClient();
        using var provider = Services(client);
        var auditId = await SeedAsync(provider, WorkToolCommandStatuses.Dispatching, DateTime.UtcNow.AddMinutes(-2));

        var worker = new WorkToolGroupOperationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.False(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .WorkToolOperationAudits.AsNoTracking().SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.DeliveryUnknown, stored.Status);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Accepted_operation_without_a_result_expires_to_result_timeout()
    {
        var client = new RecordingClient();
        using var provider = Services(client);
        var auditId = await SeedAsync(provider, WorkToolCommandStatuses.Accepted, null, DateTime.UtcNow.AddMinutes(-11));

        var worker = new WorkToolGroupOperationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.False(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .WorkToolOperationAudits.AsNoTracking().SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.ResultTimeout, stored.Status);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Explicit_rejection_is_terminal_and_is_not_redispatched()
    {
        var client = new RecordingClient
        {
            NextResult = new(false, null, "worktool_code_1001", false)
        };
        using var provider = Services(client);
        var auditId = await SeedAsync(provider, WorkToolCommandStatuses.Queued, null);
        var worker = new WorkToolGroupOperationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));
        Assert.False(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .WorkToolOperationAudits.AsNoTracking().SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.Rejected, stored.Status);
        Assert.Equal(1, client.Calls);
    }

    private ServiceProvider Services(RecordingClient client) => new ServiceCollection()
        .AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_fixture.ConnectionString))
        .AddSingleton<ISecretProtector, PassThroughProtector>()
        .AddSingleton<IWorkToolClient>(client)
        .BuildServiceProvider();

    private static async Task<Guid> SeedAsync(
        ServiceProvider provider,
        string status,
        DateTime? leaseExpiry,
        DateTime? acceptedAtUtc = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity
        {
            Name = Guid.NewGuid().ToString("N"), WorkToolRobotId = Guid.NewGuid().ToString("N"), CallbackSecretHash = "test"
        };
        var command = new WorkToolGroupOperationRequest(robot.Id, WorkToolGroupOperationKind.Rename, "技术部", [], "新名称");
        var audit = new WorkToolOperationAuditEntity
        {
            RobotConfigId = robot.Id, OperatorName = "admin", Operation = "Rename", WorkToolCommandNumber = 207,
            SanitizedRequestJson = "{}", Status = status, EncryptedCommandJson = JsonSerializer.Serialize(command),
            LeaseOwner = status == WorkToolCommandStatuses.Dispatching ? "crashed" : null, LeaseExpiresAtUtc = leaseExpiry,
            AcceptedAtUtc = acceptedAtUtc
        };
        database.AddRange(robot, audit);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return audit.Id;
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class RecordingClient : IWorkToolClient
    {
        public int Calls { get; private set; }
        public WorkToolCommandSubmission NextResult { get; init; } = new(true, "fake-group-command", null, false);
        public Task<WorkToolCommandSubmission> ExecuteGroupOperationAsync(WorkToolGroupOperationRequest request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(NextResult); }
        public Task<WorkToolCommandSubmission> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkToolSendResult> TestConnectionAsync(Guid robotConfigId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkToolSendResult> BindCallbackAsync(Guid robotConfigId, int type, Uri callbackUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
