namespace WechatRobot.Application.Jobs;

public sealed record DurableReplyLane(string Name, string JobType);

public static class DurableReplyLanePlan
{
    public static IReadOnlyList<DurableReplyLane> All { get; } =
    [
        new("group-1", "ProcessInboundMessage"),
        new("group-2", "ProcessInboundMessage"),
        new("group-3", "ProcessInboundMessage"),
        new("group-4", "ProcessInboundMessage"),
        new("private-1", "ProcessPrivateMessage"),
        new("private-ingest-1", "ProcessPrivateKnowledgeIngest")
    ];
}
