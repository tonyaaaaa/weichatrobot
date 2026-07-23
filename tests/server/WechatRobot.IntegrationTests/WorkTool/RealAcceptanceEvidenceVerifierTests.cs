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
    [InlineData("notification-missing")]
    [InlineData("semantic-retrieval-mismatch")]
    public void Missing_or_mismatched_backend_state_fails_with_stable_sanitized_code(string code)
    {
        var snapshot = CompleteSnapshot() with
        {
            IdentityMatched = code != "identity-mismatch",
            DuplicateInboundCount = code == "duplicate-callback-mismatch" ? 2 : 1,
            EmployeeNotificationCompleted = code != "notification-missing",
            LaterSemanticRetrievalMatched = code != "semantic-retrieval-mismatch"
        };

        var exception = Assert.Throws<RealAcceptanceVerificationException>(() => RealAcceptanceEvidenceVerifier.Verify(snapshot));

        Assert.Equal(code, exception.Code);
        Assert.Equal(code, exception.Message);
        Assert.DoesNotContain("http", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RealAcceptanceEvidenceSnapshot CompleteSnapshot()
    {
        var from = new DateTime(2026, 7, 23, 0, 0, 0, DateTimeKind.Utc);
        var ids = Enumerable.Range(1, 11).Select(_ => Guid.NewGuid()).ToArray();
        return new(
            from, from.AddHours(1), true, true, false, true, 1, true, true, true,
            true, true, true, true, true, true,
            ids.Select((id, index) => new RealAcceptanceEvidence($"condition-{index}", id, from.AddMinutes(index + 1))).ToArray());
    }
}
