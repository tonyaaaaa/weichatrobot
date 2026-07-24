namespace WechatRobot.Application.WorkTool;

public interface IWorkToolCredentialResolver
{
    Task<string> ResolveRobotIdAsync(Guid robotConfigId, CancellationToken cancellationToken);
}

public interface IWorkToolClient
{
    Task<WorkToolCommandSubmission> SendTextAsync(
        WorkToolSendRequest request,
        CancellationToken cancellationToken);

    Task<WorkToolCommandSubmission> ExecuteGroupOperationAsync(
        WorkToolGroupOperationRequest request,
        CancellationToken cancellationToken);

    Task<WorkToolRobotSnapshot> GetRobotAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken);

    Task<WorkToolOnlineSnapshot> GetOnlineAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken);

    Task<WorkToolMessageCallbackConfiguration> ConfigureMessageCallbackAsync(
        Guid robotConfigId,
        WorkToolMessageCallbackRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkToolEventCallbackRegistration>> ListEventCallbacksAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken);

    Task<WorkToolCallbackMutationResult> BindEventCallbackAsync(
        Guid robotConfigId,
        int type,
        Uri callbackUrl,
        CancellationToken cancellationToken);

    Task<WorkToolCallbackMutationResult> DeleteEventCallbackAsync(
        Guid robotConfigId,
        int type,
        CancellationToken cancellationToken);
}

public sealed record WorkToolSendRequest(
    Guid RobotConfigId,
    string GroupName,
    string Text,
    string IdempotencyKey,
    IReadOnlyList<string>? AtList = null);

public sealed record WorkToolCommandSubmission(
    bool Accepted,
    string? MessageId,
    string? FailureCode,
    bool DeliveryMayHaveOccurred);

public sealed record WorkToolRobotSnapshot(
    bool Reachable,
    string? RobotId,
    bool MessageCallbackEnabled,
    bool ReplyAllEnabled,
    string? FailureCode);

public sealed record WorkToolOnlineSnapshot(bool? Online, string? FailureCode);

public sealed record WorkToolMessageCallbackRequest(
    bool OpenCallback,
    bool ReplyAll,
    Uri CallbackUrl);

public sealed record WorkToolMessageCallbackConfiguration(
    bool Configured,
    bool OpenCallback,
    bool ReplyAll,
    string? FailureCode);

public sealed record WorkToolEventCallbackRegistration(
    int Type,
    string CallbackUrl);

public sealed record WorkToolCallbackMutationResult(
    bool Succeeded,
    string? FailureCode);

public sealed record WorkToolApiFailure(string Code);

public enum WorkToolGroupOperationKind
{
    Create,
    AddMembers,
    RemoveMembers,
    Rename,
    UpdateAnnouncement
}

public sealed record WorkToolGroupOperationRequest(
    Guid RobotConfigId,
    WorkToolGroupOperationKind Kind,
    string GroupIdentifier,
    IReadOnlyList<string> MemberDisplayNames,
    string? Value);
