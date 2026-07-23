using System.Text.Json;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class RealWorkToolAcceptanceTests
{
    private static readonly string[] RequiredEvidence =
    [
        "noAtReply", "duplicateCallback", "allowedTags", "disallowedTags", "noVisibleSource",
        "explicitTransfer", "employeeNotification", "aiPause", "humanResolution", "approval",
        "laterSemanticRetrieval"
    ];

    [Fact]
    [Trait("Category", "RealWorkToolAcceptance")]
    public async Task Explicitly_confirmed_technical_department_acceptance_records_sanitized_evidence()
    {
        var settings = OrdinarySettings.TryLoad();
        Assert.SkipUnless(settings is not null,
            "Real WorkTool acceptance is disabled. Set RUN_WORKTOOL_E2E=1 and every WORKTOOL_E2E_* value from the runbook.");

        using var client = new HttpClient { BaseAddress = settings!.BaseUrl };
        var result = await new WorkToolClient(client).TestConnectionAsync(settings.RobotId, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.FailureReason);

        var evidence = JsonSerializer.Deserialize<Dictionary<string, Guid>>(settings.AuditIdsJson)
            ?? throw new InvalidOperationException("WORKTOOL_E2E_AUDIT_IDS_JSON must be a JSON object.");
        Assert.All(RequiredEvidence, key => Assert.True(evidence.TryGetValue(key, out var id) && id != Guid.Empty, $"Missing sanitized audit ID: {key}"));

        TestContext.Current.TestOutputHelper?.WriteLine(
            "RealWorkToolAcceptance utc={0:O} target={1} auditIds={2}",
            DateTime.UtcNow, settings.TargetGroup, JsonSerializer.Serialize(evidence));
    }

    [Fact]
    [Trait("Category", "RealWorkToolAcceptance")]
    [Trait("Category", "RealWorkToolGroupMutation")]
    public async Task Separately_confirmed_type_206_and_207_group_mutations()
    {
        var settings = MutationSettings.TryLoad();
        Assert.SkipUnless(settings is not null,
            "Type 206/207 is separately disabled. Set RUN_WORKTOOL_GROUP_MUTATION_E2E=1 and every WORKTOOL_GROUP_MUTATION_* confirmation value.");

        using var client = new HttpClient { BaseAddress = settings!.BaseUrl };
        var workTool = new WorkToolClient(client);
        var create = await workTool.ExecuteGroupOperationAsync(
            new WorkToolGroupOperationRequest(settings.RobotId, WorkToolGroupOperationKind.Create, settings.NewGroupName, settings.MemberIds, settings.Announcement),
            TestContext.Current.CancellationToken);
        Assert.True(create.Succeeded, create.FailureReason);

        var modify = await workTool.ExecuteGroupOperationAsync(
            new WorkToolGroupOperationRequest(settings.RobotId, WorkToolGroupOperationKind.Rename, settings.NewGroupName, [], settings.RenamedGroupName),
            TestContext.Current.CancellationToken);
        Assert.True(modify.Succeeded, modify.FailureReason);
        TestContext.Current.TestOutputHelper?.WriteLine("RealWorkToolGroupMutation utc={0:O} commands=206,207", DateTime.UtcNow);
    }

    private sealed record OrdinarySettings(Uri BaseUrl, string RobotId, string TargetGroup, string AuditIdsJson)
    {
        public static OrdinarySettings? TryLoad()
        {
            if (!IsOne("RUN_WORKTOOL_E2E")) return null;
            var baseUrl = Required("WORKTOOL_E2E_BASE_URL");
            var robotId = Required("WORKTOOL_E2E_ROBOT_ID");
            var callbackSecret = Required("WORKTOOL_E2E_CALLBACK_SECRET");
            var target = Required("WORKTOOL_E2E_TARGET_GROUP");
            var confirmed = Required("WORKTOOL_E2E_TARGET_CONFIRMED");
            var auditIds = Required("WORKTOOL_E2E_AUDIT_IDS_JSON");
            if (baseUrl is null || robotId is null || callbackSecret is null || target is null || confirmed is null || auditIds is null
                || target != "技术部" || confirmed != target || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }
            return new OrdinarySettings(uri, robotId, target, auditIds);
        }
    }

    private sealed record MutationSettings(
        Uri BaseUrl, string RobotId, string NewGroupName, string RenamedGroupName, string Announcement, string[] MemberIds)
    {
        public static MutationSettings? TryLoad()
        {
            if (!IsOne("RUN_WORKTOOL_GROUP_MUTATION_E2E")) return null;
            var baseUrl = Required("WORKTOOL_GROUP_MUTATION_BASE_URL");
            var robotId = Required("WORKTOOL_GROUP_MUTATION_ROBOT_ID");
            var newGroup = Required("WORKTOOL_GROUP_MUTATION_NEW_GROUP");
            var renamedGroup = Required("WORKTOOL_GROUP_MUTATION_RENAMED_GROUP");
            var announcement = Required("WORKTOOL_GROUP_MUTATION_ANNOUNCEMENT");
            var members = Required("WORKTOOL_GROUP_MUTATION_MEMBER_IDS");
            var confirmed = Required("WORKTOOL_GROUP_MUTATION_TARGET_CONFIRMED");
            if (baseUrl is null || robotId is null || newGroup is null || renamedGroup is null || announcement is null || members is null
                || confirmed != newGroup || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }
            var memberIds = members.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return memberIds.Length == 0 ? null : new MutationSettings(uri, robotId, newGroup, renamedGroup, announcement, memberIds);
        }
    }

    private static bool IsOne(string name) => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
    private static string? Required(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
        ? null
        : Environment.GetEnvironmentVariable(name);
}
