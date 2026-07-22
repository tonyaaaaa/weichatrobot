using WechatRobot.Application.Conversations;

namespace WechatRobot.Application.Handoffs;

public sealed record HandoffTriggerOptions(IReadOnlyList<string> ExplicitTransferPhrases, int RepeatedSystemFailureThreshold = 3,
    Guid DefaultAssigneeUserId = default, string DefaultAssigneeTarget = "人工客服")
{
    public const string SectionName = "Handoff";
}

public sealed record HandoffTrigger(string ReasonCode);

public sealed class HandoffTriggerEvaluator(HandoffTriggerOptions options)
{
    public HandoffTrigger? Evaluate(string question, AnswerDecisionKind decision, int consecutiveSystemFailures)
    {
        if (options.ExplicitTransferPhrases.Any(phrase => !string.IsNullOrWhiteSpace(phrase) && question.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            return new("explicit_transfer");
        if (decision == AnswerDecisionKind.Handoff) return new("policy_handoff");
        if (decision == AnswerDecisionKind.SystemFailure && consecutiveSystemFailures >= options.RepeatedSystemFailureThreshold)
            return new("repeated_system_failure");
        return null;
    }
}
