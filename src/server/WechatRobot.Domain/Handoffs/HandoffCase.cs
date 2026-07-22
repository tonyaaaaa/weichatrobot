namespace WechatRobot.Domain.Handoffs;

public enum HandoffState { AIActive, WaitingHuman, HumanHandling, Resolved }
public enum PauseScope { Group, Sender }

public sealed class InvalidHandoffTransitionException(string message) : InvalidOperationException(message);

public sealed class HandoffCase
{
    private HandoffCase(Guid id, Guid groupProfileId, Guid questionMessageId, string reason, string evidenceJson,
        PauseScope pauseScope, string? stableSenderId, DateTime createdAtUtc)
    {
        Id = id;
        GroupProfileId = groupProfileId;
        QuestionMessageId = questionMessageId;
        Reason = reason;
        EvidenceJson = evidenceJson;
        PauseScope = pauseScope;
        StableSenderId = stableSenderId;
        State = HandoffState.WaitingHuman;
        CreatedAtUtc = UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }
    public Guid GroupProfileId { get; }
    public Guid QuestionMessageId { get; }
    public string Reason { get; }
    public string EvidenceJson { get; }
    public PauseScope PauseScope { get; }
    public string? StableSenderId { get; }
    public HandoffState State { get; private set; }
    public Guid? AssigneeUserId { get; private set; }
    public string? FinalAnswer { get; private set; }
    public DateTime CreatedAtUtc { get; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static HandoffCase Start(Guid id, Guid groupProfileId, Guid questionMessageId, string reason, string evidenceJson,
        PauseScope pauseScope, string? stableSenderId, DateTime createdAtUtc)
    {
        if (id == Guid.Empty || groupProfileId == Guid.Empty || questionMessageId == Guid.Empty) throw new ArgumentException("Handoff identifiers are required.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A structured handoff reason is required.");
        if (pauseScope == PauseScope.Sender && string.IsNullOrWhiteSpace(stableSenderId))
            throw new ArgumentException("Sender pause requires a stable sender identifier.");
        return new(id, groupProfileId, questionMessageId, reason, evidenceJson, pauseScope, stableSenderId, createdAtUtc);
    }

    public static HandoffCase Restore(Guid id, Guid groupProfileId, Guid questionMessageId, string reason, string evidenceJson,
        PauseScope pauseScope, string? stableSenderId, HandoffState state, Guid? assigneeUserId, string? finalAnswer, DateTime createdAtUtc, DateTime updatedAtUtc)
    {
        var value = Start(id, groupProfileId, questionMessageId, reason, evidenceJson, pauseScope, stableSenderId, createdAtUtc);
        value.State = state; value.AssigneeUserId = assigneeUserId; value.FinalAnswer = finalAnswer; value.UpdatedAtUtc = updatedAtUtc;
        return value;
    }

    public bool Assign(Guid assigneeUserId, DateTime nowUtc)
    {
        if (assigneeUserId == Guid.Empty) throw new ArgumentException("Assignee is required.");
        if (State == HandoffState.HumanHandling && AssigneeUserId == assigneeUserId) return false;
        if (State is not (HandoffState.WaitingHuman or HandoffState.HumanHandling)) throw Invalid("assign");
        AssigneeUserId = assigneeUserId;
        State = HandoffState.HumanHandling;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    public bool Resolve(string finalAnswer, DateTime nowUtc)
    {
        if (State == HandoffState.Resolved && string.Equals(FinalAnswer, finalAnswer, StringComparison.Ordinal)) return false;
        if (State != HandoffState.HumanHandling) throw Invalid("resolve");
        if (string.IsNullOrWhiteSpace(finalAnswer)) throw new ArgumentException("A final answer is required.");
        FinalAnswer = finalAnswer.Trim();
        State = HandoffState.Resolved;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    public bool RestoreAi(DateTime nowUtc)
    {
        if (State == HandoffState.AIActive) return false;
        if (State != HandoffState.Resolved) throw Invalid("restore AI");
        State = HandoffState.AIActive;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    public bool IsPaused(Guid groupProfileId, string? stableSenderId) => State is HandoffState.WaitingHuman or HandoffState.HumanHandling &&
        groupProfileId == GroupProfileId && (PauseScope == PauseScope.Group ||
            !string.IsNullOrWhiteSpace(stableSenderId) && string.Equals(stableSenderId, StableSenderId, StringComparison.Ordinal));

    private InvalidHandoffTransitionException Invalid(string action) => new($"Cannot {action} a handoff in state {State}.");
}
