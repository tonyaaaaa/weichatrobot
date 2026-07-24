using System.Text.Json;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;

namespace WechatRobot.Application.Handoffs;

public interface IHandoffOrchestrator
{
    Task<bool> IsPausedAsync(ConversationProcessingRequest request, CancellationToken token);
    Task<bool> TryStartExplicitAsync(ConversationProcessingRequest request, CancellationToken token);
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

    public async Task<bool> TryStartExplicitAsync(ConversationProcessingRequest request, CancellationToken token)
    {
        var trigger = triggers.Evaluate(request.Question, AnswerDecisionKind.Answer, 0);
        return trigger is not null && await StartAsync(request, trigger, null, token);
    }

    public async Task<bool> HandleDecisionAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token)
    {
        var failures = result.Decision.Kind == AnswerDecisionKind.SystemFailure
            ? 1 + await store.CountRecentSystemFailuresAsync(request.GroupProfileId, Math.Max(1, options.RepeatedSystemFailureThreshold - 1), token)
            : 0;
        var trigger = triggers.Evaluate(request.Question, result.Decision.Kind, failures);
        if (trigger is null) return false;
        return await StartAsync(request, trigger, result.Audit, token);
    }

    private async Task<bool> StartAsync(
        ConversationProcessingRequest request,
        HandoffTrigger trigger,
        RetrievalAuditDraft? audit,
        CancellationToken token)
    {
        Guid? assignee = options.DefaultAssigneeUserId == Guid.Empty ? null : options.DefaultAssigneeUserId;
        var requestedSenderPause = request.HandoffPausePolicy == HandoffPausePolicy.Sender;
        var degradedToGroup = requestedSenderPause && string.IsNullOrWhiteSpace(request.StableSenderId);
        var pauseScope = requestedSenderPause && !degradedToGroup ? HandoffPauseScope.Sender : HandoffPauseScope.Group;
        var stableSenderId = pauseScope == HandoffPauseScope.Sender ? request.StableSenderId : null;
        await handoffs.StartAsync(new(request.MessageId, request.RobotConfigId, request.GroupProfileId, request.WorkToolRobotId, request.GroupName,
            trigger.ReasonCode, BuildEvidenceJson(audit, degradedToGroup),
            pauseScope, stableSenderId, assignee, options.DefaultAssigneeTarget, $"handoff:{request.MessageId:D}"), token);
        return true;
    }

    private static string BuildEvidenceJson(RetrievalAuditDraft? audit, bool degradedToGroup) => JsonSerializer.Serialize(new
    {
        evidence = audit?.Evidence.Select(item => new
        {
            item.DocumentId,
            item.VersionId,
            item.ChunkId,
            item.PageNumber,
            item.Similarity,
            tagIds = item.TagIds
        }),
        audit?.FailureCode,
        audit?.ConfidenceValue,
        pauseScopeDegradation = degradedToGroup ? "stable_sender_id_unavailable_group_pause" : null
    });
}
