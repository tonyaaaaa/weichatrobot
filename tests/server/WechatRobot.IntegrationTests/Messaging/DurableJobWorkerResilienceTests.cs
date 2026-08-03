using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.PrivateChat;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Messaging;

public sealed class DurableJobWorkerResilienceTests
{
    [Fact]
    public async Task Session_busy_is_deferred_without_counting_a_failure()
    {
        var job = new LeasedDurableJob(
            Guid.NewGuid(),
            "ProcessPrivateMessage",
            "{}",
            2,
            "owner");
        var repository = new FakeRepository(job);
        var services = new ServiceCollection()
            .AddSingleton<IDurableJobRepository>(repository)
            .AddSingleton<IPrivateChatProcessor>(
                new ThrowingPrivateProcessor(
                    new ConversationSessionBusyException("busy")))
            .BuildServiceProvider();
        var worker = new DurableJobWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            renewalInterval: TimeSpan.FromMilliseconds(10));

        Assert.True(await worker.ProcessOnceAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(1, repository.DeferCount);
        Assert.Equal(0, repository.FailCount);
    }

    [Fact]
    public async Task Long_processing_renews_the_durable_job_lease()
    {
        var job = new LeasedDurableJob(
            Guid.NewGuid(),
            "ProcessPrivateMessage",
            "{}",
            0,
            "owner");
        var repository = new FakeRepository(job);
        var services = new ServiceCollection()
            .AddSingleton<IDurableJobRepository>(repository)
            .AddSingleton<IPrivateChatProcessor>(
                new DelayedPrivateProcessor(TimeSpan.FromMilliseconds(80)))
            .BuildServiceProvider();
        var worker = new DurableJobWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            renewalInterval: TimeSpan.FromMilliseconds(10));

        Assert.True(await worker.ProcessOnceAsync(
            TestContext.Current.CancellationToken));

        Assert.True(repository.RenewCount > 0);
        Assert.Equal(1, repository.CompleteCount);
    }

    [Fact]
    public async Task Lane_recovers_after_transient_lease_failure()
    {
        var repository = new FakeRepository(null)
        {
            ThrowFirstPrivateLease = true
        };
        var services = new ServiceCollection()
            .AddSingleton<IDurableJobRepository>(repository)
            .BuildServiceProvider();
        var worker = new DurableJobWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            renewalInterval: TimeSpan.FromMilliseconds(10));

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (repository.PrivateLeaseCount < 2 && DateTime.UtcNow < timeout)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(repository.PrivateLeaseCount >= 2);
    }

    [Fact]
    public async Task Valid_configuration_reload_resizes_lanes_and_invalid_reload_is_ignored()
    {
        var repository = new FakeRepository(null);
        var services = new ServiceCollection()
            .AddSingleton<IDurableJobRepository>(repository)
            .BuildServiceProvider();
        var monitor = new MutableOptionsMonitor<DurableReplyLaneOptions>(
            LaneOptions(group: 1));
        var worker = new DurableJobWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            configuredLaneOptions: monitor,
            renewalInterval: TimeSpan.FromMilliseconds(10));

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => repository.LeaseCount("group-1") > 0);
        Assert.Equal(0, repository.LeaseCount("group-2"));

        monitor.Set(LaneOptions(group: 2));
        await WaitUntilAsync(() => repository.LeaseCount("group-2") > 0);
        var beforeInvalid = repository.LeaseCount("group-2");

        monitor.Set(LaneOptions(group: 0));
        await WaitUntilAsync(() => repository.LeaseCount("group-2") > beforeInvalid);

        monitor.Set(LaneOptions(group: 1));
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var afterScaleDown = repository.LeaseCount("group-2");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(afterScaleDown, repository.LeaseCount("group-2"));

        monitor.Set(LaneOptions(group: 3));
        await WaitUntilAsync(() => repository.LeaseCount("group-3") > 0);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    private static DurableReplyLaneOptions LaneOptions(int group) => new()
    {
        GroupLaneCount = group,
        PrivateLaneCount = 1,
        PrivateKnowledgeIngestLaneCount = 1
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < timeout)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    private sealed class ThrowingPrivateProcessor(Exception exception)
        : IPrivateChatProcessor
    {
        public Task ProcessAsync(
            LeasedDurableJob job,
            CancellationToken cancellationToken) => Task.FromException(exception);
    }

    private sealed class DelayedPrivateProcessor(TimeSpan delay)
        : IPrivateChatProcessor
    {
        public async Task ProcessAsync(
            LeasedDurableJob job,
            CancellationToken cancellationToken) =>
            await Task.Delay(delay, cancellationToken);
    }

    private sealed class FakeRepository(LeasedDurableJob? job)
        : IDurableJobRepository
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int>
            leaseCounts = new(StringComparer.Ordinal);
        private int returned;
        private int privateLeaseCount;
        public bool ThrowFirstPrivateLease { get; init; }
        public int PrivateLeaseCount => privateLeaseCount;
        public int DeferCount { get; private set; }
        public int FailCount { get; private set; }
        public int RenewCount { get; private set; }
        public int CompleteCount { get; private set; }
        public int LeaseCount(string laneName) =>
            leaseCounts.GetValueOrDefault(laneName);

        public Task<LeasedDurableJob?> LeaseNextJobAsync(
            string jobType,
            string leaseOwner,
            DateTime nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var laneName = leaseOwner[(leaseOwner.LastIndexOf('-') + 1)..];
            if (leaseOwner.Contains("private-ingest-", StringComparison.Ordinal))
                laneName = $"private-ingest-{laneName}";
            else if (leaseOwner.Contains("private-", StringComparison.Ordinal))
                laneName = $"private-{laneName}";
            else if (leaseOwner.Contains("group-", StringComparison.Ordinal))
                laneName = $"group-{laneName}";
            leaseCounts.AddOrUpdate(laneName, 1, static (_, count) => count + 1);
            if (jobType == "ProcessPrivateMessage")
            {
                var count = Interlocked.Increment(ref privateLeaseCount);
                if (ThrowFirstPrivateLease && count == 1)
                    throw new InvalidOperationException("transient lease failure");
            }
            if (job is null || job.JobType != jobType || Interlocked.Exchange(ref returned, 1) != 0)
                return Task.FromResult<LeasedDurableJob?>(null);
            return Task.FromResult<LeasedDurableJob?>(job with { LeaseOwner = leaseOwner });
        }

        public Task CompleteJobAsync(Guid jobId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken)
        {
            CompleteCount++;
            return Task.CompletedTask;
        }

        public Task FailJobAsync(LeasedDurableJob value, string reason, DateTime failedAtUtc, CancellationToken cancellationToken)
        {
            FailCount++;
            return Task.CompletedTask;
        }

        public Task DeferJobAsync(LeasedDurableJob value, string reason, DateTime deferredAtUtc, TimeSpan retryDelay, CancellationToken cancellationToken)
        {
            DeferCount++;
            return Task.CompletedTask;
        }

        public Task<bool> RenewJobLeaseAsync(LeasedDurableJob value, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            RenewCount++;
            return Task.FromResult(true);
        }

        public Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> MarkSendDispatchingAsync(LeasedSendCommand command, DateTime dispatchedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkSendDeliveryUnknownAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkSendRejectedAsync(LeasedSendCommand command, string reason, DateTime rejectedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkSendAcceptedAsync(LeasedSendCommand command, string workToolMessageId, DateTime acceptedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, TimeSpan? retryDelay, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutableOptionsMonitor<T>(T current) : IOptionsMonitor<T>
    {
        private readonly object sync = new();
        private readonly List<Action<T, string?>> listeners = [];
        public T CurrentValue { get; private set; } = current;
        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (sync) listeners.Add(listener);
            return new CallbackRegistration(() =>
            {
                lock (sync) listeners.Remove(listener);
            });
        }

        public void Set(T value)
        {
            Action<T, string?>[] callbacks;
            lock (sync)
            {
                CurrentValue = value;
                callbacks = listeners.ToArray();
            }
            foreach (var callback in callbacks) callback(value, null);
        }

        private sealed class CallbackRegistration(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
