using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.Worker.Jobs;

public sealed class RobotSendWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, TimeSpan? leaseDuration = null, TimeSpan? leaseRenewalInterval = null) : BackgroundService
{
    private readonly string _leaseOwner = $"send-{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly TimeSpan _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(1);
    private readonly TimeSpan _leaseRenewalInterval = leaseRenewalInterval ?? TimeSpan.FromSeconds(20);

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var command = await repository.LeaseNextSendCommandAsync(_leaseOwner, now, _leaseDuration, cancellationToken);
        if (command is null)
        {
            return false;
        }

        var sendGateHeld = false;
        var externalDispatchStarted = false;
        try
        {
            if (!await repository.EnsureSendEnabledAsync(command, cancellationToken)) return true;
            sendGateHeld = true;
            if (!await repository.MarkSendExternalInFlightAsync(command, timeProvider.GetUtcNow().UtcDateTime, cancellationToken))
                return true;
            externalDispatchStarted = true;
            var client = scope.ServiceProvider.GetRequiredService<IWorkToolClient>();
            using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var renewal = RenewLeasesUntilSendCompletesAsync(repository, command, renewalCancellation.Token);
            WorkToolSendResult result;
            try
            {
                result = await client.SendTextAsync(new WorkToolSendRequest(command.RobotConfigId, command.GroupName, command.Text,
                    command.IdempotencyKey, command.AtList), cancellationToken);
            }
            finally
            {
                renewalCancellation.Cancel();
                await renewal;
            }
            if (result.Succeeded)
            {
                await repository.CompleteSendCommandAsync(command, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            }
            else
            {
                if (result.DeliveryMayHaveOccurred)
                    await repository.MarkSendDeliveryUncertainAsync(command, result.FailureReason ?? "WorkTool delivery outcome is unknown.", timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
                else
                    await repository.FailSendCommandAsync(command, result.FailureReason ?? "WorkTool send failed.", timeProvider.GetUtcNow().UtcDateTime, SendCommandService.GetRetryDelay(command.AttemptCount + 1), cancellationToken);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (externalDispatchStarted)
                await repository.MarkSendDeliveryUncertainAsync(command, "WorkTool delivery outcome is unknown.", timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            else
                await repository.FailSendCommandAsync(command, "WorkTool send failed before dispatch.", timeProvider.GetUtcNow().UtcDateTime, SendCommandService.GetRetryDelay(command.AttemptCount + 1), cancellationToken);
        }
        finally
        {
            if (sendGateHeld) await repository.ReleaseSendGateAsync(CancellationToken.None);
        }

        return true;
    }

    private async Task RenewLeasesUntilSendCompletesAsync(IDurableJobRepository repository, LeasedSendCommand command, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_leaseRenewalInterval, cancellationToken);
                if (!await repository.RenewSendLeasesAsync(command, timeProvider.GetUtcNow().UtcDateTime, _leaseDuration, cancellationToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
