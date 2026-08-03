namespace WechatRobot.Application.Jobs;

public sealed record DurableReplyLane(string Name, string JobType, int Ordinal);

public static class DurableReplyLanePlan
{
    public static IReadOnlyList<DurableReplyLane> All { get; } =
        Create(new DurableReplyLaneOptions());

    public static IReadOnlyList<DurableReplyLane> Maximum { get; } =
        Create(new DurableReplyLaneOptions
        {
            GroupLaneCount = DurableReplyLaneOptions.MaximumGroupLaneCount,
            PrivateLaneCount = DurableReplyLaneOptions.MaximumPrivateLaneCount,
            PrivateKnowledgeIngestLaneCount =
                DurableReplyLaneOptions.MaximumPrivateKnowledgeIngestLaneCount
        });

    public static IReadOnlyList<DurableReplyLane> Create(
        DurableReplyLaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.TryValidate(out var error))
            throw new ArgumentOutOfRangeException(nameof(options), error);

        return Enumerable.Range(1, options.GroupLaneCount)
            .Select(index => new DurableReplyLane(
                $"group-{index}",
                "ProcessInboundMessage",
                index))
            .Concat(Enumerable.Range(1, options.PrivateLaneCount)
                .Select(index => new DurableReplyLane(
                    $"private-{index}",
                    "ProcessPrivateMessage",
                    index)))
            .Concat(Enumerable.Range(
                    1,
                    options.PrivateKnowledgeIngestLaneCount)
                .Select(index => new DurableReplyLane(
                    $"private-ingest-{index}",
                    "ProcessPrivateKnowledgeIngest",
                    index)))
            .ToArray();
    }
}
