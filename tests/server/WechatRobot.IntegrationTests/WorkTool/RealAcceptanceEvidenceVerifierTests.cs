using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class RealAcceptanceEvidenceVerifierTests
{
    [Fact]
    public void Complete_correlated_snapshot_returns_all_required_conditions()
    {
        var snapshot = CompleteSnapshot();

        var evidence = RealAcceptanceEvidenceVerifier.Verify(snapshot);

        Assert.Equal(11, evidence.Count);
        Assert.All(evidence, item => Assert.NotEqual(Guid.Empty, item.AuditId));
        Assert.All(evidence, item => Assert.InRange(item.TimestampUtc, snapshot.FromUtc, snapshot.ToUtc));
    }

    [Theory]
    [InlineData("identity-mismatch")]
    [InlineData("duplicate-callback-mismatch")]
    [InlineData("disallowed-group-tag-mismatch")]
    [InlineData("disallowed-tag-not-enabled")]
    [InlineData("disallowed-tag-global-public")]
    [InlineData("forbidden-probe-document-mismatch")]
    [InlineData("disallowed-probe-callback-mismatch")]
    [InlineData("disallowed-probe-processing-mismatch")]
    [InlineData("disallowed-retrieval-filter-mismatch")]
    [InlineData("disallowed-effective-scope-mismatch")]
    [InlineData("global-public-scope-mismatch")]
    [InlineData("disallowed-outcome-mismatch")]
    [InlineData("notification-missing")]
    [InlineData("semantic-retrieval-mismatch")]
    public void Missing_or_mismatched_backend_state_fails_with_stable_sanitized_code(string code)
    {
        var snapshot = CompleteSnapshot() with
        {
            IdentityMatched = code != "identity-mismatch",
            DuplicateInboundCount = code == "duplicate-callback-mismatch" ? 2 : 1,
            DisallowedGroupExcludesTag = code != "disallowed-group-tag-mismatch",
            DisallowedTagEnabled = code != "disallowed-tag-not-enabled",
            DisallowedTagPrivate = code != "disallowed-tag-global-public",
            ForbiddenProbeDocumentMatched = code != "forbidden-probe-document-mismatch",
            DisallowedProbeCallbackMatched = code != "disallowed-probe-callback-mismatch",
            DisallowedProbeProcessingCompleted = code != "disallowed-probe-processing-mismatch",
            DisallowedRetrievalFilterMatched = code != "disallowed-retrieval-filter-mismatch",
            DisallowedEffectiveScopeExcludesTag = code != "disallowed-effective-scope-mismatch",
            EnabledGlobalPublicScopeMatched = code != "global-public-scope-mismatch",
            DisallowedOutcomeMatched = code != "disallowed-outcome-mismatch",
            EmployeeNotificationCompleted = code != "notification-missing",
            LaterSemanticRetrievalMatched = code != "semantic-retrieval-mismatch"
        };

        var exception = Assert.Throws<RealAcceptanceVerificationException>(() => RealAcceptanceEvidenceVerifier.Verify(snapshot));

        Assert.Equal(code, exception.Code);
        Assert.Equal(code, exception.Message);
        Assert.DoesNotContain("http", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exact_operation_matches_every_sanitized_field_and_operator_despite_concurrent_same_command_rows()
    {
        var robotId = Guid.NewGuid();
        var createId = Guid.NewGuid();
        var renameId = Guid.NewGuid();
        var audits = new[]
        {
            OperationAudit(Guid.NewGuid(), "Rename", 207, "other-admin", robotId, "group-a", 0, "EMPTY", 7, "DECOY"),
            OperationAudit(createId, "Create", 206, "model-admin", robotId, "group-a", 2, "MEMBERS", 12, "ANNOUNCEMENT"),
            OperationAudit(renameId, "Rename", 207, "model-admin", robotId, "group-a", 0, "EMPTY", 7, "RENAMED")
        };

        Assert.True(RealAcceptanceEvidenceVerifier.ExactOperation(audits,
            new(createId, "Succeeded", 206, robotId, "group-a", "Create", 2, "MEMBERS", 12, "ANNOUNCEMENT", "model-admin")));
        Assert.True(RealAcceptanceEvidenceVerifier.ExactOperation(audits,
            new(renameId, "Succeeded", 207, robotId, "group-a", "Rename", 0, "EMPTY", 7, "RENAMED", "model-admin")));
        Assert.False(RealAcceptanceEvidenceVerifier.ExactOperation(audits,
            new(renameId, "Succeeded", 207, robotId, "group-a", "Create", 0, "EMPTY", 7, "RENAMED", "model-admin")));
    }

    private static WorkToolOperationAuditEntity OperationAudit(Guid id, string operation, int command, string operatorName,
        Guid robotId, string groupIdentifier, int memberCount, string memberDisplayNamesHash, int valueLength, string valueHash) => new()
    {
        Id = id,
        Operation = operation,
        WorkToolCommandNumber = command,
        OperatorName = operatorName,
        Status = "Succeeded",
        SanitizedRequestJson = $$"""
            {"robotConfigId":"{{robotId:D}}","kind":"{{operation}}","groupIdentifier":"{{groupIdentifier}}","memberCount":{{memberCount}},"memberDisplayNamesHash":"{{memberDisplayNamesHash}}","valueLength":{{valueLength}},"valueHash":"{{valueHash}}"}
            """
    };

    private static RealAcceptanceEvidenceSnapshot CompleteSnapshot()
    {
        var from = new DateTime(2026, 7, 23, 0, 0, 0, DateTimeKind.Utc);
        var ids = Enumerable.Range(1, 11).Select(_ => Guid.NewGuid()).ToArray();
        return new(
            from, from.AddHours(1), true, true, false, true, 1, true,
            true, true, true, true, true, true, true,
            true, true, true, true, true, true,
            ids.Select((id, index) => new RealAcceptanceEvidence($"condition-{index}", id, from.AddMinutes(index + 1))).ToArray());
    }
}
