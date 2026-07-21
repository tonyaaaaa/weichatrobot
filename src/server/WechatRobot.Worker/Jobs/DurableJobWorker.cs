using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;

namespace WechatRobot.Worker.Jobs;

public sealed class DurableJobWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    private readonly string _leaseOwner = $"durable-{Environment.MachineName}-{Guid.NewGuid():N}";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var job = await repository.LeaseNextJobAsync(_leaseOwner, now, LeaseDuration, cancellationToken);
        if (job is null)
        {
            return false;
        }

        try
        {
            var processor = scope.ServiceProvider.GetRequiredService<InboundMessageProcessor>();
            await processor.ProcessAsync(job, cancellationToken);
            await repository.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await repository.FailJobAsync(job, "Inbound processing failed.", timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await ProcessOnceAsync(stoppingToken);
            if (!processed)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
    }
}
