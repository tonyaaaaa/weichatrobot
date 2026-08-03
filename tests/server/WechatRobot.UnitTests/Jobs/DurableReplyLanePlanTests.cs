using WechatRobot.Application.Jobs;

namespace WechatRobot.UnitTests.Jobs;

public sealed class DurableReplyLanePlanTests
{
    [Fact]
    public void Default_reply_processing_has_independent_bounded_lanes()
    {
        var lanes = DurableReplyLanePlan.All;

        Assert.Equal(
            8,
            lanes.Count(x => x.JobType == "ProcessInboundMessage"));
        Assert.Equal(
            4,
            lanes.Count(x => x.JobType == "ProcessPrivateMessage"));
        Assert.Equal(
            4,
            lanes.Count(x => x.JobType == "ProcessPrivateKnowledgeIngest"));
        Assert.Equal(lanes.Count, lanes.Select(x => x.Name).Distinct().Count());
    }

    [Fact]
    public void Configured_reply_processing_uses_each_requested_lane_count()
    {
        var options = new DurableReplyLaneOptions
        {
            GroupLaneCount = 3,
            PrivateLaneCount = 2,
            PrivateKnowledgeIngestLaneCount = 1
        };

        var lanes = DurableReplyLanePlan.Create(options);

        Assert.Equal(3, lanes.Count(x => x.JobType == "ProcessInboundMessage"));
        Assert.Equal(2, lanes.Count(x => x.JobType == "ProcessPrivateMessage"));
        Assert.Single(lanes, x => x.JobType == "ProcessPrivateKnowledgeIngest");
        Assert.Equal(lanes.Count, lanes.Select(x => x.Name).Distinct().Count());
    }

    [Theory]
    [InlineData(0, 1, 1, "group_lane_count_out_of_range")]
    [InlineData(33, 1, 1, "group_lane_count_out_of_range")]
    [InlineData(1, 0, 1, "private_lane_count_out_of_range")]
    [InlineData(1, 17, 1, "private_lane_count_out_of_range")]
    [InlineData(1, 1, 0, "private_ingest_lane_count_out_of_range")]
    [InlineData(1, 1, 9, "private_ingest_lane_count_out_of_range")]
    public void Invalid_lane_counts_are_rejected(
        int group,
        int privateReply,
        int privateIngest,
        string expectedError)
    {
        var options = new DurableReplyLaneOptions
        {
            GroupLaneCount = group,
            PrivateLaneCount = privateReply,
            PrivateKnowledgeIngestLaneCount = privateIngest
        };

        Assert.False(options.TryValidate(out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Maximum_plan_contains_every_unique_bounded_lane()
    {
        var lanes = DurableReplyLanePlan.Maximum;

        Assert.Equal(32 + 16 + 8, lanes.Count);
        Assert.Equal(lanes.Count, lanes.Select(x => x.Name).Distinct().Count());
    }
}
