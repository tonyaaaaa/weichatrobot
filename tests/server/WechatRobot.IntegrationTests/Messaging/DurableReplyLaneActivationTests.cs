using WechatRobot.Application.Jobs;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Messaging;

public sealed class DurableReplyLaneActivationTests
{
    [Fact]
    public async Task Disabled_lane_wakes_after_valid_scale_up()
    {
        var activation = new DurableReplyLaneActivation(Options(group: 1));
        var lane = DurableReplyLanePlan.Maximum.Single(x => x.Name == "group-2");

        var waiting = activation.WaitUntilEnabledAsync(
            lane,
            TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        Assert.True(activation.TryUpdate(Options(group: 2), out var error));
        await waiting;

        Assert.Equal(string.Empty, error);
        Assert.True(activation.IsEnabled(lane));
    }

    [Fact]
    public async Task Invalid_update_keeps_last_valid_counts_and_later_valid_update_recovers()
    {
        var activation = new DurableReplyLaneActivation(Options(group: 2));
        var second = DurableReplyLanePlan.Maximum.Single(x => x.Name == "group-2");
        var third = DurableReplyLanePlan.Maximum.Single(x => x.Name == "group-3");

        Assert.False(activation.TryUpdate(Options(group: 0), out var error));
        Assert.Equal("group_lane_count_out_of_range", error);
        Assert.True(activation.IsEnabled(second));
        Assert.False(activation.IsEnabled(third));

        var waiting = activation.WaitUntilEnabledAsync(
            third,
            TestContext.Current.CancellationToken);
        Assert.True(activation.TryUpdate(Options(group: 3), out error));
        await waiting;

        Assert.Equal(string.Empty, error);
        Assert.True(activation.IsEnabled(third));
    }

    [Fact]
    public async Task Scale_down_does_not_cancel_an_already_enabled_operation()
    {
        var activation = new DurableReplyLaneActivation(Options(group: 2));
        var lane = DurableReplyLanePlan.Maximum.Single(x => x.Name == "group-2");
        using var operation = new CancellationTokenSource();

        await activation.WaitUntilEnabledAsync(lane, operation.Token);
        Assert.True(activation.TryUpdate(Options(group: 1), out _));

        Assert.False(operation.IsCancellationRequested);
        Assert.False(activation.IsEnabled(lane));
    }

    private static DurableReplyLaneOptions Options(int group) => new()
    {
        GroupLaneCount = group,
        PrivateLaneCount = 1,
        PrivateKnowledgeIngestLaneCount = 1
    };
}
