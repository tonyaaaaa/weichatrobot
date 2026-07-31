using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.PrivateChat;

namespace WechatRobot.Worker.Jobs;

public sealed class DurableJobWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<DurableJobWorker>? configuredLogger = null,
    TimeSpan? renewalInterval = null) : BackgroundService
{
    private readonly string _leaseOwner = $"durable-{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly ILogger<DurableJobWorker> _logger =
        configuredLogger ?? NullLogger<DurableJobWorker>.Instance;
    private readonly TimeSpan _renewalInterval =
        renewalInterval ?? TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SessionBusyRetryDelay =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LaneFailureDelay = TimeSpan.FromSeconds(1);

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var jobType in new[]
                 {
                     "ProcessInboundMessage",
                     "ProcessPrivateMessage",
                     "ProcessPrivateKnowledgeIngest"
                 })
        {
            if (await ProcessOnceAsync(jobType, _leaseOwner, cancellationToken))
                return true;
        }
        return false;
    }

    private async Task<bool> ProcessOnceAsync(
        string jobType,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var job = await repository.LeaseNextJobAsync(
            jobType,
            leaseOwner,
            now,
            LeaseDuration,
            cancellationToken);
        if (job is null) return false;

        var started = Stopwatch.GetTimestamp();
        var queueAgeMs = job.CreatedAtUtc is { } createdAtUtc
            ? Math.Max(0, (long)(now - createdAtUtc).TotalMilliseconds)
            : 0;
        using var processingCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var renewalCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLost = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = RenewLeaseAsync(
            job,
            renewalCancellation.Token,
            () =>
            {
                leaseLost.TrySetResult();
                processingCancellation.Cancel();
            });

        try
        {
            if (job.JobType == "ProcessPrivateKnowledgeIngest")
                await scope.ServiceProvider.GetRequiredService<IPrivateKnowledgeIngestProcessor>().ProcessAsync(job, processingCancellation.Token);
            else if (job.JobType == "ProcessPrivateMessage")
                await scope.ServiceProvider.GetRequiredService<IPrivateChatProcessor>().ProcessAsync(job, processingCancellation.Token);
            else
                await scope.ServiceProvider.GetRequiredService<InboundMessageProcessor>().ProcessAsync(job, processingCancellation.Token);
            await repository.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, processingCancellation.Token);
            _logger.LogInformation(
                "Durable reply job completed. JobType={JobType} QueueAgeMs={QueueAgeMs} ProcessingMs={ProcessingMs}",
                job.JobType,
                queueAgeMs,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (ConversationSessionBusyException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            await repository.DeferJobAsync(
                job,
                "Conversation session is busy.",
                timeProvider.GetUtcNow().UtcDateTime,
                SessionBusyRetryDelay,
                cancellationToken);
            _logger.LogInformation(
                exception,
                "Durable reply job deferred. JobType={JobType} QueueAgeMs={QueueAgeMs} ProcessingMs={ProcessingMs}",
                job.JobType,
                queueAgeMs,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested
                  && leaseLost.Task.IsCompleted)
        {
            _logger.LogWarning(
                "Durable reply job stopped after lease ownership was lost. JobType={JobType} QueueAgeMs={QueueAgeMs} ProcessingMs={ProcessingMs}",
                job.JobType,
                queueAgeMs,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await repository.FailJobAsync(job, "Inbound processing failed.", timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            _logger.LogWarning(
                exception,
                "Durable reply job failed. JobType={JobType} QueueAgeMs={QueueAgeMs} ProcessingMs={ProcessingMs}",
                job.JobType,
                queueAgeMs,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            renewalCancellation.Cancel();
            try
            {
                await renewal;
            }
            catch (OperationCanceledException)
                when (renewalCancellation.IsCancellationRequested)
            {
            }
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lanes = DurableReplyLanePlan.All.Select(lane => RunLaneAsync(
            lane,
            $"{_leaseOwner}-{lane.Name}",
            stoppingToken));
        await Task.WhenAll(lanes);
    }

    private async Task RunLaneAsync(
        DurableReplyLane lane,
        string leaseOwner,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOnceAsync(
                    lane.JobType,
                    leaseOwner,
                    stoppingToken);
                if (!processed)
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(250),
                        stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Durable reply lane recovered from a transient failure. Lane={Lane} JobType={JobType}",
                    lane.Name,
                    lane.JobType);
                await Task.Delay(LaneFailureDelay, stoppingToken);
            }
        }
    }

    private async Task RenewLeaseAsync(
        LeasedDurableJob job,
        CancellationToken cancellationToken,
        Action onLeaseLost)
    {
        var expectedExpiry = timeProvider.GetUtcNow().UtcDateTime.Add(
            LeaseDuration);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_renewalInterval, cancellationToken);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider
                    .GetRequiredService<IDurableJobRepository>();
                if (!await repository.RenewJobLeaseAsync(
                        job,
                        now,
                        LeaseDuration,
                        cancellationToken))
                {
                    onLeaseLost();
                    return;
                }
                expectedExpiry = now.Add(LeaseDuration);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Durable reply lease renewal failed transiently. JobType={JobType}",
                    job.JobType);
                if (now >= expectedExpiry)
                {
                    onLeaseLost();
                    return;
                }
            }
        }
    }
}
