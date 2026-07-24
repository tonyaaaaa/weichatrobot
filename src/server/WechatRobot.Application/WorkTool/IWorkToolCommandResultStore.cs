namespace WechatRobot.Application.WorkTool;

public interface IWorkToolCommandResultStore
{
    Task<WorkToolResultTarget?> FindAsync(Guid robotConfigId, string workToolMessageId, CancellationToken token);
    Task<WorkToolResultApplyOutcome> ApplyAsync(WorkToolResultTarget target, WorkToolExecutionResult result, CancellationToken token);
    Task RecordOrphanAsync(Guid robotConfigId, WorkToolExecutionResult result, CancellationToken token);
}

public enum WorkToolResultTargetKind
{
    SendCommand,
    GroupOperation
}

public sealed record WorkToolResultTarget(
    WorkToolResultTargetKind Kind,
    Guid Id,
    Guid RobotConfigId,
    string WorkToolMessageId);

public sealed record WorkToolExecutionResult(
    string WorkToolMessageId,
    string FinalStatus,
    int ErrorCode,
    DateTime ResultAtUtc,
    IReadOnlyList<string> SuccessList,
    IReadOnlyList<string> FailList);

public enum WorkToolResultApplyOutcome
{
    Applied,
    AlreadyApplied,
    Conflict
}
