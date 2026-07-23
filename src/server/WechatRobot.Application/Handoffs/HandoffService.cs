namespace WechatRobot.Application.Handoffs;

public enum HandoffPauseScope { Group, Sender }

public sealed record StartHandoffCommand(Guid QuestionMessageId, Guid RobotConfigId, Guid GroupProfileId, string WorkToolRobotId,
    string GroupName, string ReasonCode, string EvidenceJson, HandoffPauseScope PauseScope, string? StableSenderId,
    Guid? AssigneeUserId, string AssigneeTarget, string IdempotencyKey, string? RequestReason = null);
public sealed record HandoffRecord(Guid Id, string State, Guid? AssigneeUserId, int Version);
public sealed record ManualStartHandoffCommand(Guid QuestionMessageId, string Reason, HandoffPauseScope PauseScope, Guid? AssigneeUserId,
    string IdempotencyKey, Guid AuthenticatedActorUserId);
public sealed record KnowledgeCandidateRecord(Guid Id, Guid HandoffCaseId, string Question, string Answer, string Status, int Version);

public interface IHandoffStore
{
    Task<HandoffRecord> StartAsync(StartHandoffCommand command, DateTime nowUtc, CancellationToken token);
    Task RecordUnverifiedWorkToolMessageAsync(Guid handoffId, string externalMessageId, string displayName, string text, DateTime nowUtc, CancellationToken token);
    Task<KnowledgeCandidateRecord> ResolveAsync(Guid handoffId, Guid authenticatedActorUserId, string finalAnswer, int expectedVersion, DateTime nowUtc, CancellationToken token);
    Task<HandoffRecord> AssignAsync(Guid handoffId, Guid authenticatedActorUserId, Guid assigneeUserId, int expectedVersion, DateTime nowUtc, CancellationToken token);
    Task<HandoffRecord> RestoreAiAsync(Guid handoffId, Guid authenticatedActorUserId, int expectedVersion, DateTime nowUtc, CancellationToken token);
    Task<bool> IsPausedAsync(Guid groupProfileId, string? stableSenderId, CancellationToken token);
    Task<int> CountRecentSystemFailuresAsync(Guid groupProfileId, int maximum, CancellationToken token);
    Task CapturePausedMessageAsync(Guid groupProfileId, string? stableSenderId, Guid conversationMessageId, string displayName, string text, DateTime nowUtc, CancellationToken token);
    Task<HandoffRecord> StartManualAsync(ManualStartHandoffCommand command, DateTime nowUtc, CancellationToken token);
}

public sealed class HandoffService(IHandoffStore store, TimeProvider timeProvider)
{
    public Task<HandoffRecord> StartAsync(StartHandoffCommand command, CancellationToken token)
    {
        if (command.PauseScope == HandoffPauseScope.Sender && string.IsNullOrWhiteSpace(command.StableSenderId))
            throw new ArgumentException("Sender-only pause requires a stable sender identifier; WorkTool display names are not identities.");
        return store.StartAsync(command, timeProvider.GetUtcNow().UtcDateTime, token);
    }
    public Task<HandoffRecord> StartManualAsync(ManualStartHandoffCommand command, CancellationToken token)
    {
        ValidateIdempotency(command.IdempotencyKey);
        return store.StartManualAsync(command with { IdempotencyKey = $"manual:{command.IdempotencyKey.Trim()}" },
            timeProvider.GetUtcNow().UtcDateTime, token);
    }

    public Task RecordUnverifiedWorkToolMessageAsync(Guid handoffId, string externalMessageId, string displayName, string text, CancellationToken token) =>
        store.RecordUnverifiedWorkToolMessageAsync(handoffId, externalMessageId, displayName, text, timeProvider.GetUtcNow().UtcDateTime, token);

    public Task<KnowledgeCandidateRecord> ResolveAsync(Guid handoffId, Guid authenticatedActorUserId, string finalAnswer, int expectedVersion, CancellationToken token)
    {
        if (authenticatedActorUserId == Guid.Empty) throw new UnauthorizedAccessException("An authenticated API actor is required.");
        if (string.IsNullOrWhiteSpace(finalAnswer)) throw new ArgumentException("A selected or edited final answer is required.");
        return store.ResolveAsync(handoffId, authenticatedActorUserId, finalAnswer.Trim(), expectedVersion, timeProvider.GetUtcNow().UtcDateTime, token);
    }

    public Task<HandoffRecord> AssignAsync(Guid id, Guid actor, Guid assignee, int version, CancellationToken token) =>
        store.AssignAsync(id, actor, assignee, version, timeProvider.GetUtcNow().UtcDateTime, token);
    public Task<HandoffRecord> RestoreAiAsync(Guid id, Guid actor, int version, CancellationToken token) =>
        store.RestoreAiAsync(id, actor, version, timeProvider.GetUtcNow().UtcDateTime, token);
    public Task<bool> IsPausedAsync(Guid groupId, string? stableSenderId, CancellationToken token) => store.IsPausedAsync(groupId, stableSenderId, token);
    public Task CapturePausedMessageAsync(Guid groupId, string? stableSenderId, Guid messageId, string displayName, string text, CancellationToken token) =>
        store.CapturePausedMessageAsync(groupId, stableSenderId, messageId, displayName, text, timeProvider.GetUtcNow().UtcDateTime, token);
    private static void ValidateIdempotency(string value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 48) throw new ArgumentException("Idempotency key is required and must not exceed 48 characters."); }
}

public sealed class HandoffConcurrencyException(string message) : Exception(message);
public sealed class HandoffStateException(string message) : Exception(message);
