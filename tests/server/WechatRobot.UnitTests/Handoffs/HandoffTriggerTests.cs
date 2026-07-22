using WechatRobot.Application.Conversations;
using WechatRobot.Application.Handoffs;

namespace WechatRobot.UnitTests.Handoffs;

public sealed class HandoffTriggerTests
{
    [Theory]
    [InlineData("请转人工客服", AnswerDecisionKind.Answer, 0, "explicit_transfer")]
    [InlineData("普通问题", AnswerDecisionKind.Handoff, 0, "policy_handoff")]
    [InlineData("普通问题", AnswerDecisionKind.SystemFailure, 3, "repeated_system_failure")]
    public void Supported_triggers_create_structured_reason(string question, AnswerDecisionKind decision, int failures, string expected)
    {
        var result = new HandoffTriggerEvaluator(new HandoffTriggerOptions(["转人工", "人工客服"], 3))
            .Evaluate(question, decision, failures);

        Assert.Equal(expected, result?.ReasonCode);
    }

    [Fact]
    public void A_single_system_failure_does_not_transfer()
    {
        Assert.Null(new HandoffTriggerEvaluator(new HandoffTriggerOptions(["转人工"], 3))
            .Evaluate("普通问题", AnswerDecisionKind.SystemFailure, 1));
    }
}
