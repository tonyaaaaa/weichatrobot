using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Health;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.Api.Dashboard;

public sealed class DashboardSummaryService(
    IDbContextFactory<WechatRobotDbContext> databaseFactory,
    IEnumerable<IComponentHealthProbe> healthProbes,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    public async Task<DashboardSummaryResponse> GetAsync(CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var robotRows = await database.RobotConfigs.AsNoTracking()
            .Select(robot => new { robot.Id, robot.IsEnabled })
            .ToArrayAsync(cancellationToken);
        var enabledRobotIds = robotRows
            .Where(robot => robot.IsEnabled)
            .Select(robot => robot.Id)
            .ToArray();

        var documents = await database.KnowledgeDocuments.AsNoTracking()
            .CountAsync(document => !document.IsDeleteRequested, cancellationToken);
        var versions = await database.KnowledgeDocumentVersions.AsNoTracking()
            .CountAsync(cancellationToken);
        var pendingCandidates = await database.KnowledgeCandidates.AsNoTracking()
            .CountAsync(candidate => candidate.Status == "pending", cancellationToken);
        var failedVersions = await database.KnowledgeDocumentVersions.AsNoTracking()
            .CountAsync(version => version.Status == "failed", cancellationToken);
        var failedIndexJobs = await database.KnowledgeIndexJobs.AsNoTracking()
            .CountAsync(job => job.Status == "failed", cancellationToken);
        var durableJobs = await StatusCountsAsync(
            database.DurableJobs.AsNoTracking().Select(job => job.Status),
            cancellationToken);
        var sendCommands = await StatusCountsAsync(
            database.SendCommands.AsNoTracking().Select(command => command.Status),
            cancellationToken);
        var deadLetters = await database.DeadLetters.AsNoTracking().CountAsync(cancellationToken);

        var robotChecksTask = Task.WhenAll(
            enabledRobotIds.Select(id => CheckRobotAsync(id, cancellationToken)));
        var readinessTask = CheckReadinessAsync(cancellationToken);
        await Task.WhenAll(robotChecksTask, readinessTask);

        var robotChecks = robotChecksTask.Result;
        return new(
            timeProvider.GetUtcNow().UtcDateTime,
            new RobotSummaryResponse(
                robotRows.Length,
                enabledRobotIds.Length,
                robotChecks.Count(check => check.Reachable),
                robotChecks.Count(check => check.Online),
                robotChecks.Count(check => check.MessageCallbackConfigured),
                robotChecks.Count(check => check.CommandResultCallbackConfigured),
                robotChecks.Count(check => check.Failed)),
            new KnowledgeSummaryResponse(
                documents,
                versions,
                pendingCandidates,
                failedVersions + failedIndexJobs),
            new OperationsSummaryResponse(durableJobs, sendCommands, deadLetters),
            readinessTask.Result);
    }

    private async Task<RobotCheck> CheckRobotAsync(Guid robotId, CancellationToken requestAborted)
    {
        var timeoutMilliseconds = configuration.GetValue(
            "Dashboard:RobotProbeTimeoutMilliseconds",
            3000);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(
            Math.Clamp(timeoutMilliseconds, 100, 10_000)));

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<RobotCallbackConfigurationService>();
            var probe = await service.ProbeAsync(robotId, deadline.Token)
                .WaitAsync(deadline.Token);
            var callbacks = await service.GetStatusAsync(robotId, deadline.Token)
                .WaitAsync(deadline.Token);
            return new(
                probe.Reachable,
                probe.Online == true,
                callbacks.MessageCallbackConfigured,
                callbacks.CommandResultCallbackConfigured,
                Failed: false);
        }
        catch (Exception) when (!requestAborted.IsCancellationRequested)
        {
            return new(false, false, false, false, Failed: true);
        }
    }

    private async Task<ReadinessSummaryResponse> CheckReadinessAsync(
        CancellationToken requestAborted)
    {
        var timeoutMilliseconds = configuration.GetValue(
            "Health:ProbeTimeoutMilliseconds",
            3000);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(
            Math.Clamp(timeoutMilliseconds, 100, 10_000)));

        var checks = await Task.WhenAll(healthProbes.Select(probe =>
            CheckHealthAsync(probe, deadline.Token, requestAborted)));
        var status = checks.Any(check => check.Required && check.Status == "failed")
            ? "failed"
            : checks.Any(check => check.Status == "failed") ? "degraded" : "healthy";
        return new(status, checks);
    }

    private static async Task<ReadinessComponentResponse> CheckHealthAsync(
        IComponentHealthProbe probe,
        CancellationToken deadline,
        CancellationToken requestAborted)
    {
        try
        {
            var result = await probe.CheckAsync(deadline).WaitAsync(deadline);
            return new(
                result.Name,
                result.State == ComponentHealthState.Healthy ? "healthy" : "failed",
                result.Required,
                result.Detail);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            return new(probe.Name, "failed", probe.Required, "timeout");
        }
        catch (Exception) when (!requestAborted.IsCancellationRequested)
        {
            return new(probe.Name, "failed", probe.Required, "unavailable");
        }
    }

    private static async Task<IReadOnlyDictionary<string, int>> StatusCountsAsync(
        IQueryable<string> statuses,
        CancellationToken cancellationToken)
    {
        var rows = await statuses
            .GroupBy(status => status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);
        return rows.ToDictionary(
            row => row.Status,
            row => row.Count,
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed record RobotCheck(
        bool Reachable,
        bool Online,
        bool MessageCallbackConfigured,
        bool CommandResultCallbackConfigured,
        bool Failed);
}

public sealed record DashboardSummaryResponse(
    DateTime CheckedAtUtc,
    RobotSummaryResponse Robots,
    KnowledgeSummaryResponse Knowledge,
    OperationsSummaryResponse Operations,
    ReadinessSummaryResponse Readiness);

public sealed record RobotSummaryResponse(
    int Total,
    int Enabled,
    int Reachable,
    int Online,
    int MessageCallbackConfigured,
    int CommandResultCallbackConfigured,
    int FailedChecks);

public sealed record KnowledgeSummaryResponse(
    int Documents,
    int Versions,
    int PendingCandidates,
    int FailedTasks);

public sealed record OperationsSummaryResponse(
    IReadOnlyDictionary<string, int> DurableJobs,
    IReadOnlyDictionary<string, int> SendCommands,
    int DeadLetters);

public sealed record ReadinessSummaryResponse(
    string Status,
    IReadOnlyCollection<ReadinessComponentResponse> Components);

public sealed record ReadinessComponentResponse(
    string Name,
    string Status,
    bool Required,
    string? Detail);
