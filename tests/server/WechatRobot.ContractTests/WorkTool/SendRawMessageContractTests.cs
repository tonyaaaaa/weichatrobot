using System.Net;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class SendRawMessageContractTests
{
    [Fact]
    public async Task SendTextAsync_maps_exact_sendRawMessage_request_and_success_response()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"code\":0,\"message\":\"accepted\",\"data\":\"command-1\"}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fake.worktool.test/") };
        var sut = new WorkToolClient(client, new FixedCredentials());

        var result = await sut.SendTextAsync(new WorkToolSendRequest(Guid.NewGuid(), "Support Group", "fixed reply", "idem-1", ["张工"]), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("/wework/sendRawMessage?robotId=robot-7", handler.PathAndQuery);
        using var json = JsonDocument.Parse(handler.Body);
        Assert.Equal(2, json.RootElement.GetProperty("socketType").GetInt32());
        var command = json.RootElement.GetProperty("list")[0];
        Assert.Equal(203, command.GetProperty("type").GetInt32());
        Assert.Equal("Support Group", command.GetProperty("titleList")[0].GetString());
        Assert.Equal("fixed reply", command.GetProperty("receivedContent").GetString());
        Assert.Equal("张工", command.GetProperty("atList")[0].GetString());
    }

    [Fact]
    public async Task SendTextAsync_maps_nonzero_worktool_code_to_failure()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.OK, "{\"code\":1001,\"message\":\"rejected\"}")) { BaseAddress = new Uri("https://fake.worktool.test/") };
        var sut = new WorkToolClient(client, new FixedCredentials());

        var result = await sut.SendTextAsync(new WorkToolSendRequest(Guid.NewGuid(), "Support Group", "fixed reply", "idem-1"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("rejected", result.FailureReason);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public string? Method { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            PathAndQuery = request.RequestUri!.PathAndQuery;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FixedCredentials : IWorkToolCredentialResolver
    {
        public Task<string> ResolveRobotIdAsync(Guid robotConfigId, CancellationToken cancellationToken) =>
            Task.FromResult("robot-7");
    }
}
