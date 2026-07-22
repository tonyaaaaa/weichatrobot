using System.Text.Json;
using WechatRobot.Application.Conversations;

namespace WechatRobot.Application.Handoffs;

public interface IHandoffOrchestrator
{
    Task<bool> IsPausedAsync(ConversationProcessingRequest request, CancellationToken token);
    Task<bool> HandleDecisionAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token);
}

public sealed class HandoffOrchestrator(HandoffService handoffs, IHandoffStore store, HandoffTriggerEvaluator triggers, HandoffTriggerOptions options) : IHandoffOrchestrator
{
    public async Task<bool> IsPausedAsync(ConversationProcessingRequest request, CancellationToken token)
    {
        if (!await handoffs.IsPausedAsync(request.GroupProfileId, request.StableSenderId, token)) return false;
        await handoffs.CapturePausedMessageAsync(request.GroupProfileId, request.StableSenderId, request.MessageId,
            request.SenderDisplayName, request.Question, token);
        return true;
    }

    public async Task<bool> HandleDecisionAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token)
    {
        var failures = result.Decision.Kind == AnswerDecisionKind.SystemFailure
            ? 1 + await store.CountRecentSystemFailuresAsync(request.GroupProfileId, Math.Max(1, options.RepeatedSystemFailureThreshold - 1), token)
            : 0;
        var trigger = triggers.Evaluate(request.Question, result.Decision.Kind, failures);
        if (trigger is null) return false;
        Guid? assignee = options.DefaultAssigneeUserId == Guid.Empty ? null : options.DefaultAssigneeUserId;
        await handoffs.StartAsync(new(request.MessageId, request.RobotConfigId, request.GroupProfileId, request.WorkToolRobotId, request.GroupName,
            trigger.ReasonCode, JsonSerializer.Serialize(new { result.Audit.Evidence, result.Audit.FailureCode, result.Audit.ConfidenceValue }),
            HandoffPauseScope.Group, null, assignee, options.DefaultAssigneeTarget, $"handoff:{request.MessageId:D}"), token);
        return true;
    }
}
