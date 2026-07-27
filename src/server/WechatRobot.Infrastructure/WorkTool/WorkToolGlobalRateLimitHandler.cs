using WechatRobot.Application.WorkTool;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolGlobalRateLimitHandler(
    IWorkToolGlobalRateLimiter limiter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var lease = await limiter.AcquireAsync(
            $"{request.Method.Method}:{request.RequestUri?.AbsolutePath}",
            cancellationToken);
        if (!lease.Acquired)
            throw new WorkToolRateLimitException(
                lease.FailureCode ?? "worktool_global_rate_limited");

        return await base.SendAsync(request, cancellationToken);
    }
}

public sealed class WorkToolRateLimitException(string failureCode)
    : HttpRequestException("WorkTool global egress permit was not acquired.")
{
    public string FailureCode { get; } = failureCode;
}
