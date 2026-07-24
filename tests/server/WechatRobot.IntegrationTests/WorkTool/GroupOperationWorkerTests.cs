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
    public async Task Queued_operation_is_dispatched_once_and_completed()
    {
        var client = new RecordingClient();
        using var provider = Services(client);
        var auditId = await SeedAsync(provider, "Queued", null);

        var worker = new WorkToolGroupOperationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .WorkToolOperationAudits.AsNoTracking().SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal("Succeeded", stored.Status);
        Assert.Equal(1, client.Calls);
        Assert.NotNull(stored.ExternalDispatchStartedAtUtc);
    }

    [Fact]
    public async Task Expired_external_operation_becomes_uncertain_without_redispatch()
    {
        var client = new RecordingClient();
        using var provider = Services(client);
        var auditId = await SeedAsync(provider, "ExternalInFlight", DateTime.UtcNow.AddMinutes(-2));

        var worker = new WorkToolGroupOperationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.False(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .WorkToolOperationAudits.AsNoTracking().SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal("DeliveryUncertain", stored.Status);
        Assert.Equal(0, client.Calls);
    }

    private ServiceProvider Services(RecordingClient client) => new ServiceCollection()
        .AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_fixture.ConnectionString))
        .AddSingleton<ISecretProtector, PassThroughProtector>()
        .AddSingleton<IWorkToolClient>(client)
        .BuildServiceProvider();

    private static async Task<Guid> SeedAsync(ServiceProvider provider, string status, DateTime? leaseExpiry)
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
            LeaseOwner = status == "ExternalInFlight" ? "crashed" : null, LeaseExpiresAtUtc = leaseExpiry
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
        public Task<WorkToolCommandSubmission> ExecuteGroupOperationAsync(WorkToolGroupOperationRequest request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new WorkToolCommandSubmission(true, "fake-group-command", null, false)); }
        public Task<WorkToolCommandSubmission> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkToolSendResult> TestConnectionAsync(Guid robotConfigId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkToolSendResult> BindCallbackAsync(Guid robotConfigId, int type, Uri callbackUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
