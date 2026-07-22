namespace WechatRobot.Application.WorkTool;

public interface IWorkToolClient
{
    Task<WorkToolSendResult> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken);
    Task<WorkToolSendResult> ExecuteGroupOperationAsync(WorkToolGroupOperationRequest request, CancellationToken cancellationToken);
    Task<WorkToolSendResult> TestConnectionAsync(string workToolRobotId, CancellationToken cancellationToken);
}

public sealed record WorkToolSendRequest(string WorkToolRobotId, string GroupName, string Text, string IdempotencyKey,
    IReadOnlyList<string>? AtList = null);

public sealed record WorkToolSendResult(bool Succeeded, string? FailureReason)
{
    public static WorkToolSendResult Success() => new(true, null);
    public static WorkToolSendResult Failed(string reason) => new(false, reason);
}

public enum WorkToolGroupOperationKind { Create, AddMembers, RemoveMembers, Rename, UpdateAnnouncement }

public sealed record WorkToolGroupOperationRequest(
    string WorkToolRobotId,
    WorkToolGroupOperationKind Kind,
    string GroupIdentifier,
    IReadOnlyList<string> MemberIds,
    string? Value);
