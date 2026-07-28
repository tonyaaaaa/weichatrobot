using System.Net;
using System.Net.Http.Json;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class WorkToolHttpTransportContractTests
{
    [Fact]
    public void Windows_uses_the_sockets_transport_to_avoid_WinHTTP_gateway_failures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var handler = WorkToolHttpTransport.CreatePrimaryHandler();

        Assert.IsType<SocketsHttpHandler>(handler);
    }

    [Fact]
    public async Task Every_http_attempt_acquires_one_global_permit()
    {
        var limiter = new RecordingLimiter(WorkToolRateLimitLeaseSuccess);
        var terminal = new RecordingTerminalHandler();
        using var client = new HttpClient(
            new WorkToolGlobalRateLimitHandler(limiter) { InnerHandler = terminal })
        {
            BaseAddress = new Uri("https://worktool.example/")
        };

        await client.GetAsync("first", TestContext.Current.CancellationToken);
        await client.PostAsync(
            "second",
            JsonContent.Create(new { value = 1 }),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, limiter.AcquireCalls);
        Assert.Equal(2, terminal.SendCalls);
    }

    [Fact]
    public async Task Rejected_global_permit_prevents_the_http_attempt()
    {
        var limiter = new RecordingLimiter(
            new WorkToolRateLimitLease(false, "worktool_global_rate_limited"));
        var terminal = new RecordingTerminalHandler();
        using var client = new HttpClient(
            new WorkToolGlobalRateLimitHandler(limiter) { InnerHandler = terminal })
        {
            BaseAddress = new Uri("https://worktool.example/")
        };

        var exception = await Assert.ThrowsAsync<WorkToolRateLimitException>(() =>
            client.GetAsync("blocked", TestContext.Current.CancellationToken));

        Assert.Equal("worktool_global_rate_limited", exception.FailureCode);
        Assert.Equal(0, terminal.SendCalls);
    }

    private static readonly WorkToolRateLimitLease WorkToolRateLimitLeaseSuccess =
        new(true, null);

    private sealed class RecordingLimiter(WorkToolRateLimitLease lease)
        : IWorkToolGlobalRateLimiter
    {
        public int AcquireCalls { get; private set; }

        public Task<WorkToolRateLimitLease> AcquireAsync(
            string operation,
            CancellationToken cancellationToken)
        {
            AcquireCalls++;
            return Task.FromResult(lease);
        }
    }

    private sealed class RecordingTerminalHandler : HttpMessageHandler
    {
        public int SendCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
