namespace WechatRobot.IntegrationTests.WorkTool;

public static class RealAcceptanceEvidenceVerifier
{
    public static IReadOnlyList<RealAcceptanceEvidence> Verify(RealAcceptanceEvidenceSnapshot snapshot)
    {
        Require(snapshot.FromUtc.Kind == DateTimeKind.Utc && snapshot.ToUtc.Kind == DateTimeKind.Utc && snapshot.FromUtc < snapshot.ToUtc, "utc-window-invalid");
        Require(snapshot.IdentityMatched, "identity-mismatch");
        Require(snapshot.CallbackSecretMatched, "callback-secret-mismatch");
        Require(!snapshot.NoAtWasMentioned && snapshot.NoAtReplyCompleted, "no-at-reply-mismatch");
        Require(snapshot.DuplicateInboundCount == 1, "duplicate-callback-mismatch");
        Require(snapshot.AllowedTagEvidenceMatched, "allowed-tags-mismatch");
        Require(snapshot.DisallowedTagEvidenceMatched, "disallowed-tags-mismatch");
        Require(snapshot.NoVisibleSourceMatched, "visible-source-mismatch");
        Require(snapshot.ExplicitTransferMatched, "explicit-transfer-mismatch");
        Require(snapshot.EmployeeNotificationCompleted, "notification-missing");
        Require(snapshot.AiPauseMatched, "ai-pause-mismatch");
        Require(snapshot.HumanResolutionMatched, "human-resolution-mismatch");
        Require(snapshot.ApprovalMatched, "approval-mismatch");
        Require(snapshot.LaterSemanticRetrievalMatched, "semantic-retrieval-mismatch");
        Require(snapshot.Evidence.Count == 11, "evidence-count-mismatch");
        Require(snapshot.Evidence.All(item => item.AuditId != Guid.Empty
            && item.TimestampUtc.Kind == DateTimeKind.Utc
            && item.TimestampUtc >= snapshot.FromUtc
            && item.TimestampUtc <= snapshot.ToUtc), "evidence-window-mismatch");
        return snapshot.Evidence;
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new RealAcceptanceVerificationException(code);
    }
}

public sealed record RealAcceptanceEvidenceSnapshot(
    DateTime FromUtc,
    DateTime ToUtc,
    bool IdentityMatched,
    bool CallbackSecretMatched,
    bool NoAtWasMentioned,
    bool NoAtReplyCompleted,
    int DuplicateInboundCount,
    bool AllowedTagEvidenceMatched,
    bool DisallowedTagEvidenceMatched,
    bool NoVisibleSourceMatched,
    bool ExplicitTransferMatched,
    bool EmployeeNotificationCompleted,
    bool AiPauseMatched,
    bool HumanResolutionMatched,
    bool ApprovalMatched,
    bool LaterSemanticRetrievalMatched,
    IReadOnlyList<RealAcceptanceEvidence> Evidence);

public sealed record RealAcceptanceEvidence(string Condition, Guid AuditId, DateTime TimestampUtc);

public sealed class RealAcceptanceVerificationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
