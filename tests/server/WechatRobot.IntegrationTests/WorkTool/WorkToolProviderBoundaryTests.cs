using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class WorkToolProviderBoundaryTests
{
    [Fact]
    public async Task Operation_timeout_recovery_does_not_require_bulk_update_support()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.WorkToolOperationAudits.AddRange(
                NewAudit(
                    WorkToolCommandStatuses.Accepted,
                    acceptedAtUtc: DateTime.UtcNow.AddMinutes(-11)),
                NewAudit(
                    WorkToolCommandStatuses.Dispatching,
                    leaseOwner: "expired-owner",
                    leaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(-1)));
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var worker = new WorkToolGroupOperationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.False(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var verificationScope = provider.CreateAsyncScope();
        var stored = await verificationScope.ServiceProvider
            .GetRequiredService<WechatRobotDbContext>()
            .WorkToolOperationAudits
            .OrderBy(item => item.CreatedAtUtc)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.ResultTimeout, stored[0].Status);
        Assert.NotNull(stored[0].CompletedAtUtc);
        Assert.Equal(WorkToolCommandStatuses.DeliveryUnknown, stored[1].Status);
        Assert.Equal("external_dispatch_lease_expired", stored[1].Result);
        Assert.Null(stored[1].LeaseOwner);
        Assert.Null(stored[1].LeaseExpiresAtUtc);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        var databaseRoot = new InMemoryDatabaseRoot();
        services.AddDbContext<WechatRobotDbContext>(builder =>
            builder.UseInMemoryDatabase(databaseName, databaseRoot)
                .ReplaceService<IDatabaseProvider, ProviderWithoutBulkUpdateSupport>()
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        return services;
    }

    private static WorkToolOperationAuditEntity NewAudit(
        string status,
        DateTime? acceptedAtUtc = null,
        string? leaseOwner = null,
        DateTime? leaseExpiresAtUtc = null) =>
        new()
        {
            OperatorName = "provider-boundary",
            Operation = "Rename",
            WorkToolCommandNumber = 207,
            SanitizedRequestJson = "{}",
            Status = status,
            AcceptedAtUtc = acceptedAtUtc,
            LeaseOwner = leaseOwner,
            LeaseExpiresAtUtc = leaseExpiresAtUtc,
            CreatedAtUtc = DateTime.UtcNow
        };

    private sealed class ProviderWithoutBulkUpdateSupport : IDatabaseProvider
    {
        public string Name => "ProviderWithoutBulkUpdateSupport";

        public bool IsConfigured(IDbContextOptions options) => true;
    }
}
