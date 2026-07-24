namespace WechatRobot.Application.WorkTool;

public interface IWorkToolCredentialResolver
{
    Task<string> ResolveRobotIdAsync(Guid robotConfigId, CancellationToken cancellationToken);
}

public interface IWorkToolClient
{
    Task<WorkToolSendResult> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken);
    Task<WorkToolSendResult> ExecuteGroupOperationAsync(WorkToolGroupOperationRequest request, CancellationToken cancellationToken);
    Task<WorkToolSendResult> TestConnectionAsync(Guid robotConfigId, CancellationToken cancellationToken);
    Task<WorkToolSendResult> BindCallbackAsync(Guid robotConfigId, int type, Uri callbackUrl, CancellationToken cancellationToken);
}

public sealed record WorkToolSendRequest(Guid RobotConfigId, string GroupName, string Text, string IdempotencyKey,
    IReadOnlyList<string>? AtList = null);

public sealed record WorkToolSendResult(bool Succeeded, string? FailureReason, bool DeliveryMayHaveOccurred = false)
{
    public static WorkToolSendResult Success() => new(true, null);
    public static WorkToolSendResult Failed(string reason, bool deliveryMayHaveOccurred = false) => new(false, reason, deliveryMayHaveOccurred);
}

public enum WorkToolGroupOperationKind { Create, AddMembers, RemoveMembers, Rename, UpdateAnnouncement }

public sealed record WorkToolGroupOperationRequest(
    Guid RobotConfigId,
    WorkToolGroupOperationKind Kind,
    string GroupIdentifier,
    IReadOnlyList<string> MemberIds,
    string? Value);
