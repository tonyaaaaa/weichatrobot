namespace WechatRobot.Application.WorkTool;

public interface IWorkToolGlobalRateLimiter
{
    Task<WorkToolRateLimitLease> AcquireAsync(
        string operation,
        CancellationToken cancellationToken);
}

public sealed record WorkToolRateLimitLease(
    bool Acquired,
    string? FailureCode);
