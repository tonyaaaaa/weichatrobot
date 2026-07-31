using WechatRobot.Application.Jobs;

namespace WechatRobot.UnitTests.Jobs;

public sealed class DurableReplyLanePlanTests
{
    [Fact]
    public void Reply_processing_has_independent_bounded_lanes()
    {
        var lanes = DurableReplyLanePlan.All;

        Assert.Equal(
            4,
            lanes.Count(x => x.JobType == "ProcessInboundMessage"));
        Assert.Single(
            lanes,
            x => x.JobType == "ProcessPrivateMessage");
        Assert.Single(
            lanes,
            x => x.JobType == "ProcessPrivateKnowledgeIngest");
        Assert.Equal(lanes.Count, lanes.Select(x => x.Name).Distinct().Count());
    }
}
