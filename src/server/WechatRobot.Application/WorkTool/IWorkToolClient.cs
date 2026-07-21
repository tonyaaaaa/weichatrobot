namespace WechatRobot.Application.WorkTool;

public interface IWorkToolClient
{
    Task<WorkToolSendResult> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken);
}

public sealed record WorkToolSendRequest(string WorkToolRobotId, string GroupName, string Text, string IdempotencyKey);

public sealed record WorkToolSendResult(bool Succeeded, string? FailureReason)
{
    public static WorkToolSendResult Success() => new(true, null);
    public static WorkToolSendResult Failed(string reason) => new(false, reason);
}
