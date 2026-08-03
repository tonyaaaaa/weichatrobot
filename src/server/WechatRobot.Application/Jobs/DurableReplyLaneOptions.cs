namespace WechatRobot.Application.Jobs;

public sealed class DurableReplyLaneOptions
{
    public const string SectionName = "ReplyProcessing";
    public const int DefaultGroupLaneCount = 8;
    public const int DefaultPrivateLaneCount = 4;
    public const int DefaultPrivateKnowledgeIngestLaneCount = 4;
    public const int MaximumGroupLaneCount = 32;
    public const int MaximumPrivateLaneCount = 16;
    public const int MaximumPrivateKnowledgeIngestLaneCount = 8;

    public int GroupLaneCount { get; set; } = DefaultGroupLaneCount;
    public int PrivateLaneCount { get; set; } = DefaultPrivateLaneCount;
    public int PrivateKnowledgeIngestLaneCount { get; set; } =
        DefaultPrivateKnowledgeIngestLaneCount;

    public bool TryValidate(out string error)
    {
        if (GroupLaneCount is < 1 or > MaximumGroupLaneCount)
        {
            error = "group_lane_count_out_of_range";
            return false;
        }

        if (PrivateLaneCount is < 1 or > MaximumPrivateLaneCount)
        {
            error = "private_lane_count_out_of_range";
            return false;
        }

        if (PrivateKnowledgeIngestLaneCount is < 1
            or > MaximumPrivateKnowledgeIngestLaneCount)
        {
            error = "private_ingest_lane_count_out_of_range";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
