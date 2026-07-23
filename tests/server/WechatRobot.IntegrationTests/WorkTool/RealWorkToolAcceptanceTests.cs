using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class RealWorkToolAcceptanceTests
{
    [Fact]
    [Trait("Category", "RealWorkToolAcceptance")]
    public async Task Explicitly_confirmed_technical_department_acceptance_verifies_correlated_backend_evidence()
    {
        var settings = OrdinarySettings.TryLoad();
        Assert.SkipUnless(settings is not null,
            "Real WorkTool acceptance is disabled. Set RUN_WORKTOOL_E2E=1 and every WORKTOOL_E2E_* value from the runbook.");

        try
        {
            await using var db = Database(settings!.ConnectionString);
            var snapshot = await LoadSnapshotAsync(db, settings, TestContext.Current.CancellationToken);
            var evidence = RealAcceptanceEvidenceVerifier.Verify(snapshot);
            foreach (var item in evidence)
                TestContext.Current.TestOutputHelper?.WriteLine("{0} utc={1:O} auditId={2:D}", item.Condition, item.TimestampUtc, item.AuditId);
        }
        catch (RealAcceptanceVerificationException exception)
        {
            Assert.Fail(exception.Code);
        }
        catch
        {
            Assert.Fail("real-acceptance-query-failed");
        }
    }

    [Fact]
    [Trait("Category", "RealWorkToolAcceptance")]
    [Trait("Category", "RealWorkToolGroupMutation")]
    public async Task Separately_confirmed_type_206_and_207_group_mutations_are_audited()
    {
        var settings = MutationSettings.TryLoad();
        Assert.SkipUnless(settings is not null,
            "Type 206/207 is separately disabled. Set RUN_WORKTOOL_GROUP_MUTATION_E2E=1 and every WORKTOOL_GROUP_MUTATION_* confirmation value.");

        try
        {
            var started = DateTime.UtcNow;
            using var api = new HttpClient { BaseAddress = settings!.ApiBaseUrl };
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.BearerToken);
            await ExecuteAuditedOperationAsync(api, new
            {
                robotConfigId = settings.RobotConfigId, kind = "Create", groupIdentifier = settings.NewGroupName,
                memberIds = settings.MemberIds, value = settings.Announcement
            }, TestContext.Current.CancellationToken);
            await ExecuteAuditedOperationAsync(api, new
            {
                robotConfigId = settings.RobotConfigId, kind = "Rename", groupIdentifier = settings.NewGroupName,
                memberIds = Array.Empty<string>(), value = settings.RenamedGroupName
            }, TestContext.Current.CancellationToken);

            await using var db = Database(settings.ConnectionString);
            var audits = await db.WorkToolOperationAudits.AsNoTracking()
                .Where(item => item.CreatedAtUtc >= started && item.Status == "Succeeded"
                    && (item.WorkToolCommandNumber == 206 || item.WorkToolCommandNumber == 207))
                .OrderBy(item => item.CreatedAtUtc).ToArrayAsync(TestContext.Current.CancellationToken);
            if (!audits.Any(item => item.WorkToolCommandNumber == 206) || !audits.Any(item => item.WorkToolCommandNumber == 207))
                throw new RealAcceptanceVerificationException("group-mutation-audit-missing");
            foreach (var audit in audits.Where(item => item.WorkToolCommandNumber is 206 or 207))
                TestContext.Current.TestOutputHelper?.WriteLine("group-mutation utc={0:O} auditId={1:D} command={2}", audit.CreatedAtUtc, audit.Id, audit.WorkToolCommandNumber);
        }
        catch (RealAcceptanceVerificationException exception)
        {
            Assert.Fail(exception.Code);
        }
        catch
        {
            Assert.Fail("group-mutation-verification-failed");
        }
    }

    private static async Task ExecuteAuditedOperationAsync(HttpClient api, object operation, CancellationToken token)
    {
        using var preview = await api.PostAsJsonAsync("/api/admin/worktool/group-operations/preview", operation, token);
        if (!preview.IsSuccessStatusCode) throw new RealAcceptanceVerificationException("group-mutation-preview-failed");
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync(token));
        var confirmationToken = previewJson.RootElement.GetProperty("confirmationToken").GetString();
        if (string.IsNullOrWhiteSpace(confirmationToken)) throw new RealAcceptanceVerificationException("group-mutation-confirmation-missing");
        using var execute = await api.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation, confirmationToken }, token);
        if (!execute.IsSuccessStatusCode) throw new RealAcceptanceVerificationException("group-mutation-execute-failed");
    }

    private static async Task<RealAcceptanceEvidenceSnapshot> LoadSnapshotAsync(
        WechatRobotDbContext db,
        OrdinarySettings settings,
        CancellationToken token)
    {
        var manifest = settings.Manifest;
        var robot = await db.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.WorkToolRobotId == settings.RobotId, token);
        var group = robot is null ? null : await db.GroupProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RobotConfigId == robot.Id && item.Name == settings.TargetGroup, token);
        var identityMatched = robot is not null && group is not null;
        var callbackSecretMatched = robot is not null && SecretMatches(settings.CallbackSecret, robot.CallbackSecretHash);
        if (!identityMatched) return EmptySnapshot(manifest, identityMatched, callbackSecretMatched);

        var noAt = await MessageAsync(db, robot!.Id, group!.Id, manifest.NoAtMessageId, manifest, token);
        var duplicate = await MessageAsync(db, robot.Id, group.Id, manifest.DuplicateMessageId, manifest, token);
        var allowed = await MessageAsync(db, robot.Id, group.Id, manifest.AllowedTagMessageId, manifest, token);
        var disallowed = await MessageAsync(db, robot.Id, group.Id, manifest.DisallowedTagMessageId, manifest, token);
        var transfer = await MessageAsync(db, robot.Id, group.Id, manifest.TransferMessageId, manifest, token);
        var later = await MessageAsync(db, robot.Id, group.Id, manifest.LaterSemanticMessageId, manifest, token);
        var noAtJob = noAt is null ? null : await db.DurableJobs.AsNoTracking().SingleOrDefaultAsync(item => item.RelatedConversationMessageId == noAt.Id, token);
        var noAtWasMentioned = noAtJob is null || WasMentioned(noAtJob.PayloadJson);
        var noAtAudit = noAt is null ? null : await AuditAsync(db, noAt.Id, token);
        var noAtAnswer = noAt is null ? null : await AnswerAsync(db, noAt.Id, token);
        var noAtSends = await db.SendCommands.AsNoTracking().Where(item => item.RobotConfigId == robot.Id && item.GroupProfileId == group.Id
                && item.CreatedAtUtc >= manifest.FromUtc && item.CreatedAtUtc <= manifest.ToUtc && item.Status == "completed")
            .OrderBy(item => item.CreatedAtUtc).ToArrayAsync(token);
        var noAtSend = noAtAnswer is null ? null : noAtSends.FirstOrDefault(item => SendMatches(item.PayloadJson, group.Name, noAtAnswer.Text));
        var duplicateCount = await db.ConversationMessages.AsNoTracking().CountAsync(item =>
            item.RobotConfigId == robot.Id && item.WorkToolMessageId == manifest.DuplicateMessageId, token);
        var allowedAudit = allowed is null ? null : await AuditAsync(db, allowed.Id, token);
        var disallowedAudit = disallowed is null ? null : await AuditAsync(db, disallowed.Id, token);
        var allowedAnswer = allowed is null ? null : await AnswerAsync(db, allowed.Id, token);
        var transferHandoff = transfer is null ? null : await db.HandoffCases.AsNoTracking()
            .SingleOrDefaultAsync(item => item.QuestionMessageId == transfer.Id && item.ReasonCode == "explicit_transfer", token);
        var transitions = transferHandoff is null ? [] : await db.HandoffTransitions.AsNoTracking()
            .Where(item => item.HandoffCaseId == transferHandoff.Id).OrderBy(item => item.Sequence).ToArrayAsync(token);
        var notificationCandidates = transferHandoff is null ? [] : await db.SendCommands.AsNoTracking()
            .Where(item => item.RobotConfigId == robot.Id && item.GroupProfileId == group.Id
                && item.CreatedAtUtc >= transferHandoff.CreatedAtUtc && item.CreatedAtUtc <= manifest.ToUtc)
            .OrderBy(item => item.CreatedAtUtc).ToArrayAsync(token);
        var notification = transferHandoff is null ? null : notificationCandidates.FirstOrDefault(item => NotificationMatches(item.PayloadJson, transferHandoff.Id));
        var candidate = transferHandoff is null ? null : await db.KnowledgeCandidates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.HandoffCaseId == transferHandoff.Id, token);
        var review = candidate is null ? null : await db.KnowledgeReviews.AsNoTracking()
            .Where(item => item.KnowledgeCandidateId == candidate.Id && item.Decision == "approve")
            .OrderByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(token);
        var laterAudit = later is null ? null : await AuditAsync(db, later.Id, token);

        var evidence = new[]
        {
            Evidence("noAtReply", noAtAudit?.Id, noAtAudit?.CreatedAtUtc),
            Evidence("duplicateCallback", duplicate?.Id, duplicate?.CreatedAtUtc),
            Evidence("allowedTags", allowedAudit?.Id, allowedAudit?.CreatedAtUtc),
            Evidence("disallowedTags", disallowedAudit?.Id, disallowedAudit?.CreatedAtUtc),
            Evidence("noVisibleSource", allowedAudit?.Id, allowedAudit?.CreatedAtUtc),
            Evidence("explicitTransfer", transferHandoff?.Id, transferHandoff?.CreatedAtUtc),
            Evidence("employeeNotification", notification?.Id, notification?.CreatedAtUtc),
            Evidence("aiPause", transitions.FirstOrDefault(item => item.ToState == "WaitingHuman")?.Id,
                transitions.FirstOrDefault(item => item.ToState == "WaitingHuman")?.CreatedAtUtc),
            Evidence("humanResolution", transitions.FirstOrDefault(item => item.ToState == "Resolved")?.Id,
                transitions.FirstOrDefault(item => item.ToState == "Resolved")?.CreatedAtUtc),
            Evidence("approval", review?.Id, review?.CreatedAtUtc),
            Evidence("laterSemanticRetrieval", laterAudit?.Id, laterAudit?.CreatedAtUtc)
        };

        return new(manifest.FromUtc, manifest.ToUtc, true, callbackSecretMatched, noAtWasMentioned,
            noAtAudit is not null && noAtAnswer is not null && noAtSend is not null,
            duplicateCount,
            HasEvidence(allowedAudit) && EvidenceContainsTag(allowedAudit!.EvidenceJson, manifest.AllowedTagId),
            disallowedAudit is not null && !HasEvidence(disallowedAudit) && disallowedAudit.Decision != "Answer",
            allowedAnswer is not null && allowedAudit is not null && !ContainsVisibleSource(allowedAnswer.Text, allowedAudit.EvidenceJson),
            transferHandoff is not null,
            notification is not null && notification.Status == "completed" && HasMention(notification.PayloadJson),
            transitions.Any(item => item.FromState == "AIActive" && item.ToState == "WaitingHuman"),
            transferHandoff is not null && !string.IsNullOrWhiteSpace(transferHandoff.FinalAnswer)
                && transitions.Any(item => item.ToState == "Resolved"),
            candidate is not null && review is not null && candidate.Status is "approved_pending_index" or "indexing" or "published",
            candidate?.KnowledgeDocumentVersionId is { } versionId && laterAudit is not null && EvidenceContainsVersion(laterAudit.EvidenceJson, versionId),
            evidence);
    }

    private static RealAcceptanceEvidenceSnapshot EmptySnapshot(RealEvidenceManifest manifest, bool identity, bool secret) =>
        new(manifest.FromUtc, manifest.ToUtc, identity, secret, true, false, 0, false, false, false, false, false, false, false, false, false, []);

    private static async Task<ConversationMessageEntity?> MessageAsync(WechatRobotDbContext db, Guid robotId, Guid groupId, string externalId,
        RealEvidenceManifest manifest, CancellationToken token) =>
        await db.ConversationMessages.AsNoTracking().SingleOrDefaultAsync(item => item.RobotConfigId == robotId && item.GroupProfileId == groupId
            && item.WorkToolMessageId == externalId && item.CreatedAtUtc >= manifest.FromUtc && item.CreatedAtUtc <= manifest.ToUtc, token);
    private static Task<RetrievalAuditEntity?> AuditAsync(WechatRobotDbContext db, Guid messageId, CancellationToken token) =>
        db.RetrievalAudits.AsNoTracking().SingleOrDefaultAsync(item => item.ConversationMessageId == messageId, token);
    private static Task<ConversationMessageEntity?> AnswerAsync(WechatRobotDbContext db, Guid messageId, CancellationToken token) =>
        db.ConversationMessages.AsNoTracking().SingleOrDefaultAsync(item => item.InReplyToMessageId == messageId && item.Direction == "outbound", token);
    private static RealAcceptanceEvidence Evidence(string condition, Guid? id, DateTime? at) =>
        new(condition, id ?? Guid.Empty, at ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
    private static bool HasEvidence(RetrievalAuditEntity? audit) => audit is not null && JsonDocument.Parse(audit.EvidenceJson).RootElement.GetArrayLength() > 0;
    private static bool EvidenceContainsTag(string json, Guid tagId) => json.Contains(tagId.ToString("D"), StringComparison.OrdinalIgnoreCase);
    private static bool EvidenceContainsVersion(string json, Guid versionId) => json.Contains(versionId.ToString("D"), StringComparison.OrdinalIgnoreCase);
    private static bool ContainsVisibleSource(string text, string evidenceJson)
    {
        if (text.Contains("来源", StringComparison.OrdinalIgnoreCase) || text.Contains("source", StringComparison.OrdinalIgnoreCase)) return true;
        using var evidence = JsonDocument.Parse(evidenceJson);
        return evidence.RootElement.EnumerateArray()
            .SelectMany(item => item.EnumerateObject())
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .Select(property => property.Value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
            .Any(value => text.Contains(value!, StringComparison.OrdinalIgnoreCase));
    }
    private static bool HasMention(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.TryGetProperty("AtList", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0;
    }
    private static bool SendMatches(string payload, string groupName, string answer)
    {
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        return root.TryGetProperty("GroupName", out var group) && group.GetString() == groupName
            && root.TryGetProperty("Text", out var text) && text.GetString() == answer;
    }
    private static bool NotificationMatches(string payload, Guid handoffId) =>
        payload.Contains(handoffId.ToString("N"), StringComparison.OrdinalIgnoreCase) && HasMention(payload);
    private static bool WasMentioned(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        return !json.RootElement.TryGetProperty("WasMentioned", out var value) || value.GetBoolean();
    }
    private static bool SecretMatches(string secret, string hash)
    {
        try
        {
            var expected = Convert.FromHexString(hash);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }
    private static WechatRobotDbContext Database(string connectionString)
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(connectionString).Options;
        return new WechatRobotDbContext(options);
    }

    private sealed record OrdinarySettings(string ConnectionString, string RobotId, string CallbackSecret, string TargetGroup, RealEvidenceManifest Manifest)
    {
        public static OrdinarySettings? TryLoad()
        {
            if (!IsOne("RUN_WORKTOOL_E2E")) return null;
            var connection = Required("WORKTOOL_E2E_CONNECTION_STRING");
            var robot = Required("WORKTOOL_E2E_ROBOT_ID");
            var secret = Required("WORKTOOL_E2E_CALLBACK_SECRET");
            var target = Required("WORKTOOL_E2E_TARGET_GROUP");
            var confirmed = Required("WORKTOOL_E2E_TARGET_CONFIRMED");
            var manifestJson = Required("WORKTOOL_E2E_EVIDENCE_JSON");
            if (connection is null || robot is null || secret is null || target != "技术部" || confirmed != target || manifestJson is null) return null;
            try
            {
                var manifest = JsonSerializer.Deserialize<RealEvidenceManifest>(manifestJson);
                return manifest is null || !manifest.IsComplete ? null : new(connection, robot, secret, target, manifest);
            }
            catch (JsonException) { return null; }
        }
    }

    private sealed record RealEvidenceManifest(
        DateTime FromUtc, DateTime ToUtc, string NoAtMessageId, string DuplicateMessageId,
        string AllowedTagMessageId, string DisallowedTagMessageId, string TransferMessageId,
        string LaterSemanticMessageId, Guid AllowedTagId)
    {
        public bool IsComplete => FromUtc.Kind == DateTimeKind.Utc && ToUtc.Kind == DateTimeKind.Utc && FromUtc < ToUtc
            && AllowedTagId != Guid.Empty && new[] { NoAtMessageId, DuplicateMessageId, AllowedTagMessageId, DisallowedTagMessageId,
                TransferMessageId, LaterSemanticMessageId }.All(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record MutationSettings(
        Uri ApiBaseUrl, string BearerToken, string ConnectionString, Guid RobotConfigId,
        string NewGroupName, string RenamedGroupName, string Announcement, string[] MemberIds)
    {
        public static MutationSettings? TryLoad()
        {
            if (!IsOne("RUN_WORKTOOL_GROUP_MUTATION_E2E")) return null;
            var apiBase = Required("WORKTOOL_GROUP_MUTATION_API_BASE_URL");
            var bearer = Required("WORKTOOL_GROUP_MUTATION_BEARER_TOKEN");
            var connection = Required("WORKTOOL_GROUP_MUTATION_CONNECTION_STRING");
            var robot = Required("WORKTOOL_GROUP_MUTATION_ROBOT_CONFIG_ID");
            var newGroup = Required("WORKTOOL_GROUP_MUTATION_NEW_GROUP");
            var renamed = Required("WORKTOOL_GROUP_MUTATION_RENAMED_GROUP");
            var announcement = Required("WORKTOOL_GROUP_MUTATION_ANNOUNCEMENT");
            var members = Required("WORKTOOL_GROUP_MUTATION_MEMBER_IDS");
            var confirmed = Required("WORKTOOL_GROUP_MUTATION_TARGET_CONFIRMED");
            if (apiBase is null || bearer is null || connection is null || !Guid.TryParse(robot, out var robotId) || newGroup is null
                || renamed is null || announcement is null || members is null || confirmed != newGroup
                || !Uri.TryCreate(apiBase, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            var memberIds = members.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return memberIds.Length == 0 ? null : new(uri, bearer, connection, robotId, newGroup, renamed, announcement, memberIds);
        }
    }

    private static bool IsOne(string name) => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
    private static string? Required(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
        ? null : Environment.GetEnvironmentVariable(name);
}
