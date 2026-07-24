using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;

namespace WechatRobot.Worker.Jobs;

public sealed class KnowledgeUploadWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    private readonly string _leaseOwner = $"knowledge-upload-{Environment.MachineName}-{Guid.NewGuid():N}";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var job = await repository.LeaseNextJobAsync("UploadKnowledgeDocument", _leaseOwner,
            timeProvider.GetUtcNow().UtcDateTime, LeaseDuration, cancellationToken);
        if (job is null) return false;

        try
        {
            var service = scope.ServiceProvider.GetRequiredService<DocumentUploadService>();
            var shouldComplete = await service.RecoverAsync(job, cancellationToken);
            if (shouldComplete)
                await repository.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await repository.FailJobAsync(job, "Knowledge upload recovery failed.", timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
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
