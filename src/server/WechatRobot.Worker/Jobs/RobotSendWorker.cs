using System.Collections.Concurrent;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.Worker.Jobs;

public sealed class RobotSendWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    private readonly string _leaseOwner = $"send-{Environment.MachineName}-{Guid.NewGuid():N}";
    private static readonly ConcurrentDictionary<Guid, TokenBucket> Limiters = new();
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var command = await repository.LeaseNextSendCommandAsync(_leaseOwner, now, LeaseDuration, cancellationToken);
        if (command is null)
        {
            return false;
        }

        var limiter = Limiters.GetOrAdd(command.RobotConfigId, _ => new TokenBucket(command.SendRateLimitPerMinute, timeProvider));
        if (!limiter.TryAcquire())
        {
            await repository.ReleaseSendCommandAsync(command.Id, command.LeaseOwner, limiter.NextAvailableAtUtc(), cancellationToken);
            return true;
        }

        try
        {
            var client = scope.ServiceProvider.GetRequiredService<IWorkToolClient>();
            var result = await client.SendTextAsync(new WorkToolSendRequest(command.WorkToolRobotId, command.GroupName, command.Text, command.IdempotencyKey), cancellationToken);
            if (result.Succeeded)
            {
                await repository.CompleteSendCommandAsync(command.Id, command.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            }
            else
            {
                await repository.FailSendCommandAsync(command, result.FailureReason ?? "WorkTool send failed.", timeProvider.GetUtcNow().UtcDateTime, SendCommandService.GetRetryDelay(command.AttemptCount + 1), cancellationToken);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await repository.FailSendCommandAsync(command, "WorkTool send failed.", timeProvider.GetUtcNow().UtcDateTime, SendCommandService.GetRetryDelay(command.AttemptCount + 1), cancellationToken);
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

    private sealed class TokenBucket(int limitPerMinute, TimeProvider timeProvider)
    {
        private readonly object _gate = new();
        private readonly double _capacity = limitPerMinute;
        private readonly double _tokensPerSecond = limitPerMinute / 60d;
        private double _tokens = limitPerMinute;
        private DateTimeOffset _lastRefill = timeProvider.GetUtcNow();

        public bool TryAcquire()
        {
            lock (_gate)
            {
                Refill();
                if (_tokens < 1)
                {
                    return false;
                }

                _tokens--;
                return true;
            }
        }

        public DateTime NextAvailableAtUtc()
        {
            lock (_gate)
            {
                Refill();
                return timeProvider.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(0, 1 - _tokens) / _tokensPerSecond);
            }
        }

        private void Refill()
        {
            var now = timeProvider.GetUtcNow();
            _tokens = Math.Min(_capacity, _tokens + (now - _lastRefill).TotalSeconds * _tokensPerSecond);
            _lastRefill = now;
        }
    }
}
