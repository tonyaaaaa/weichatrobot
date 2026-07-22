using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.Worker.Jobs;

public sealed class KnowledgeIndexWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    private readonly string _owner = $"knowledge-index-{Environment.MachineName}-{Guid.NewGuid():N}";

    public async Task<bool> ProcessOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var knowledge = scope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>();
        var job = await knowledge.LeaseNextAsync(_owner, timeProvider.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(5), token);
        if (job is null) return false;
        try
        {
            if (job.Operation == "cleanup")
            {
                var vectors = scope.ServiceProvider.GetRequiredService<IVectorStore>();
                await vectors.DeleteVersionAsync(new VectorCollection(job.CollectionName, job.Dimension, job.Distance), job.VersionId, token);
                await knowledge.CompleteCleanupAsync(job.Id, token);
            }
            else
            {
                await scope.ServiceProvider.GetRequiredService<KnowledgeIndexService>().IndexAsync(job.Id, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (job.Operation == "cleanup")
                await knowledge.MarkIndexFailedAsync(job.Id, exception.Message, exception is VectorStoreUnavailableException or HttpRequestException, CancellationToken.None);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessOnceAsync(stoppingToken)) await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}
