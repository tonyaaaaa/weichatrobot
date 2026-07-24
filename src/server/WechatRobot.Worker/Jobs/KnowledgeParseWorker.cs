using WechatRobot.Application.Jobs;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.Worker.Jobs;

public sealed class KnowledgeParseWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    private readonly string _leaseOwner = $"knowledge-parse-{Environment.MachineName}-{Guid.NewGuid():N}";
    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var job = await jobs.LeaseNextJobAsync("ParseKnowledgeDocument", _leaseOwner, now, TimeSpan.FromMinutes(1), cancellationToken);
        if (job is null) return false;
        try
        {
            if (await scope.ServiceProvider.GetRequiredService<KnowledgePreviewService>().GenerateFromJobAsync(job.PayloadJson, cancellationToken))
                await jobs.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        { await jobs.FailJobAsync(job, "Knowledge parsing failed.", timeProvider.GetUtcNow().UtcDateTime, cancellationToken); }
        return true;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
            if (!await ProcessOnceAsync(stoppingToken)) await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
    }
}
