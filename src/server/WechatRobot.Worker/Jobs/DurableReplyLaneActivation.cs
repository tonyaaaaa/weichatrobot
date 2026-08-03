using WechatRobot.Application.Jobs;

namespace WechatRobot.Worker.Jobs;

public sealed class DurableReplyLaneActivation
{
    private readonly object _sync = new();
    private LaneCounts _counts;
    private TaskCompletionSource _changed = CreateSignal();

    public DurableReplyLaneActivation(DurableReplyLaneOptions initialOptions)
    {
        ArgumentNullException.ThrowIfNull(initialOptions);
        if (!initialOptions.TryValidate(out var error))
            throw new ArgumentOutOfRangeException(nameof(initialOptions), error);

        _counts = LaneCounts.From(initialOptions);
    }

    public bool TryUpdate(
        DurableReplyLaneOptions options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.TryValidate(out error)) return false;

        TaskCompletionSource previousSignal;
        lock (_sync)
        {
            _counts = LaneCounts.From(options);
            previousSignal = _changed;
            _changed = CreateSignal();
        }

        previousSignal.TrySetResult();
        return true;
    }

    public bool IsEnabled(DurableReplyLane lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        lock (_sync) return IsEnabled(lane, _counts);
    }

    public async Task WaitUntilEnabledAsync(
        DurableReplyLane lane,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lane);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (IsEnabled(lane, _counts)) return;
                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    private static bool IsEnabled(DurableReplyLane lane, LaneCounts counts) =>
        lane.JobType switch
        {
            "ProcessInboundMessage" => lane.Ordinal <= counts.Group,
            "ProcessPrivateMessage" => lane.Ordinal <= counts.PrivateReply,
            "ProcessPrivateKnowledgeIngest" =>
                lane.Ordinal <= counts.PrivateKnowledgeIngest,
            _ => false
        };

    private static TaskCompletionSource CreateSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record LaneCounts(
        int Group,
        int PrivateReply,
        int PrivateKnowledgeIngest)
    {
        public static LaneCounts From(DurableReplyLaneOptions options) => new(
            options.GroupLaneCount,
            options.PrivateLaneCount,
            options.PrivateKnowledgeIngestLaneCount);
    }
}
