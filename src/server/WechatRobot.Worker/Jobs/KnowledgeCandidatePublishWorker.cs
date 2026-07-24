using WechatRobot.Application.Jobs;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.Worker.Jobs;

public sealed class KnowledgeCandidatePublishWorker(IServiceScopeFactory scopes, TimeProvider timeProvider) : BackgroundService
{
    private readonly string _owner = $"candidate-publish-{Environment.MachineName}-{Guid.NewGuid():N}";

    public async Task<bool> ProcessOnceAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var job = await jobs.LeaseNextJobAsync("PublishKnowledgeCandidate", _owner, timeProvider.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(1), token);
        if (job is null) return false;
        try
        {
            await scope.ServiceProvider.GetRequiredService<KnowledgeCandidatePublishProcessor>().ProcessAsync(job, token);
            await jobs.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, token);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            await jobs.FailJobAsync(job, "Candidate index queueing failed.", timeProvider.GetUtcNow().UtcDateTime, token);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessOnceAsync(stoppingToken)) await Task.Delay(250, stoppingToken);
        }
    }
}
