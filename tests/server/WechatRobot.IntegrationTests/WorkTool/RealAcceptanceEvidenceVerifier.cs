using System.Text.Json;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.WorkTool;

public static class RealAcceptanceEvidenceVerifier
{
    public static bool ExactOperation(IReadOnlyList<WorkToolOperationAuditEntity> audits, RealOperationExpectation expected)
    {
        var audit = audits.SingleOrDefault(item => item.Id == expected.AuditId);
        if (audit is null || audit.Status != expected.Status || audit.WorkToolCommandNumber != expected.CommandNumber
            || audit.Operation != expected.Operation || audit.OperatorName != expected.OperatorName)
            return false;
        try
        {
            using var request = JsonDocument.Parse(audit.SanitizedRequestJson);
            var root = request.RootElement;
            return root.TryGetProperty("robotConfigId", out var robot) && robot.TryGetGuid(out var robotId)
                && robotId == expected.RobotConfigId
                && root.TryGetProperty("kind", out var kind) && kind.GetString() == expected.Operation
                && root.TryGetProperty("groupIdentifier", out var group) && group.GetString() == expected.GroupIdentifier
                && root.TryGetProperty("memberCount", out var memberCount) && memberCount.GetInt32() == expected.MemberCount
                && root.TryGetProperty("memberIdsHash", out var memberIdsHash) && memberIdsHash.GetString() == expected.MemberIdsHash
                && root.TryGetProperty("valueLength", out var valueLength) && valueLength.GetInt32() == expected.ValueLength
                && root.TryGetProperty("valueHash", out var valueHash) && valueHash.GetString() == expected.ValueHash;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<RealAcceptanceEvidence> Verify(RealAcceptanceEvidenceSnapshot snapshot)
    {
        Require(snapshot.FromUtc.Kind == DateTimeKind.Utc && snapshot.ToUtc.Kind == DateTimeKind.Utc && snapshot.FromUtc < snapshot.ToUtc, "utc-window-invalid");
        Require(snapshot.IdentityMatched, "identity-mismatch");
        Require(snapshot.CallbackSecretMatched, "callback-secret-mismatch");
        Require(!snapshot.NoAtWasMentioned && snapshot.NoAtReplyCompleted, "no-at-reply-mismatch");
        Require(snapshot.DuplicateInboundCount == 1, "duplicate-callback-mismatch");
        Require(snapshot.AllowedTagEvidenceMatched, "allowed-tags-mismatch");
        Require(snapshot.DisallowedGroupExcludesTag, "disallowed-group-tag-mismatch");
        Require(snapshot.DisallowedTagEnabled, "disallowed-tag-not-enabled");
        Require(snapshot.DisallowedTagPrivate, "disallowed-tag-global-public");
        Require(snapshot.ForbiddenProbeDocumentMatched, "forbidden-probe-document-mismatch");
        Require(snapshot.DisallowedProbeCallbackMatched, "disallowed-probe-callback-mismatch");
        Require(snapshot.DisallowedProbeProcessingCompleted, "disallowed-probe-processing-mismatch");
        Require(snapshot.DisallowedRetrievalFilterMatched, "disallowed-retrieval-filter-mismatch");
        Require(snapshot.DisallowedEffectiveScopeExcludesTag, "disallowed-effective-scope-mismatch");
        Require(snapshot.EnabledGlobalPublicScopeMatched, "global-public-scope-mismatch");
        Require(snapshot.DisallowedOutcomeMatched, "disallowed-outcome-mismatch");
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
    bool DisallowedGroupExcludesTag,
    bool ForbiddenProbeDocumentMatched,
    bool DisallowedProbeCallbackMatched,
    bool DisallowedProbeProcessingCompleted,
    bool DisallowedRetrievalFilterMatched,
    bool DisallowedOutcomeMatched,
    bool NoVisibleSourceMatched,
    bool ExplicitTransferMatched,
    bool EmployeeNotificationCompleted,
    bool AiPauseMatched,
    bool HumanResolutionMatched,
    bool ApprovalMatched,
    bool LaterSemanticRetrievalMatched,
    IReadOnlyList<RealAcceptanceEvidence> Evidence,
    bool DisallowedTagEnabled = true,
    bool DisallowedTagPrivate = true,
    bool DisallowedEffectiveScopeExcludesTag = true,
    bool EnabledGlobalPublicScopeMatched = true);

public sealed record RealAcceptanceEvidence(string Condition, Guid AuditId, DateTime TimestampUtc);
public sealed record RealOperationExpectation(Guid AuditId, string Status, int CommandNumber, Guid RobotConfigId,
    string GroupIdentifier, string Operation, int MemberCount, string MemberIdsHash, int ValueLength, string ValueHash,
    string OperatorName);

public sealed class RealAcceptanceVerificationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
