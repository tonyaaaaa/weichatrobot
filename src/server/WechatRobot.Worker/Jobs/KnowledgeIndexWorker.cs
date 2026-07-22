using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.Worker.Jobs;

public sealed record KnowledgeIndexWorkerOptions(TimeSpan LeaseDuration, TimeSpan RenewalInterval)
{
    public static KnowledgeIndexWorkerOptions Default { get; } = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
}

public sealed class KnowledgeIndexWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, KnowledgeIndexWorkerOptions workerOptions) : BackgroundService
{
    private readonly string _owner = $"knowledge-index-{Environment.MachineName}-{Guid.NewGuid():N}";

    public async Task<bool> ProcessOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var knowledge = scope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>();
        var job = await knowledge.LeaseNextAsync(_owner, timeProvider.GetUtcNow().UtcDateTime, workerOptions.LeaseDuration, token);
        if (job is null) return false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var renewal = RenewUntilCompleteAsync(job, operation);
        try
        {
            if (job.Operation == "cleanup")
            {
                var vectors = scope.ServiceProvider.GetRequiredService<IVectorStore>();
                await vectors.DeleteVersionAsync(new VectorCollection(job.CollectionName, job.Dimension, job.Distance), job.VersionId, operation.Token);
                await knowledge.CompleteCleanupAsync(job.Id, job.LeaseOwner, operation.Token);
            }
            else
            {
                await scope.ServiceProvider.GetRequiredService<KnowledgeIndexService>().IndexAsync(job.Id, operation.Token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (job.Operation == "cleanup")
                await knowledge.MarkIndexFailedAsync(job.Id, job.LeaseOwner, exception.Message,
                    exception is VectorStoreUnavailableException or HttpRequestException, CancellationToken.None);
        }
        finally
        {
            operation.Cancel();
            try { await renewal; } catch (OperationCanceledException) { }
        }
        return true;
    }

    private async Task RenewUntilCompleteAsync(LeasedKnowledgeIndexJob job, CancellationTokenSource operation)
    {
        using var timer = new PeriodicTimer(workerOptions.RenewalInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(operation.Token))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>();
            if (!await service.RenewLeaseAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, workerOptions.LeaseDuration, operation.Token))
            {
                operation.Cancel();
                return;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessOnceAsync(stoppingToken)) await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}
