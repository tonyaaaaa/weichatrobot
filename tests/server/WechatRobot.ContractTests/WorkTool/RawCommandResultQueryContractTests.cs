using System.Net;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class RawCommandResultQueryContractTests
{
    [Fact]
    public async Task Query_uses_the_documented_type512_filters_and_keeps_payloads_opaque()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """
            {
              "code": 200,
              "message": "success",
              "data": [{
                "rawMsg": "{\"socketType\":2,\"list\":[{\"type\":512,\"groupName\":\"售后客户群\"}]}",
                "rawSuccess": 1,
                "errorReason": "",
                "runTime": "2026-07-27 12:00:00",
                "apiSend": 1,
                "robotId": "must-not-be-returned",
                "type": 512,
                "messageId": "member-command-1",
                "successList": "[\"opaque\"]",
                "failList": "[]",
                "timeCost": 345
              }]
            }
            """);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://fake.worktool.test/")
        };
        var sut = new WorkToolClient(http, new FixedCredentials());

        var results = await sut.ListGroupMemberSnapshotResultsAsync(
            Guid.NewGuid(),
            "member command/1",
            new DateTimeOffset(2026, 7, 27, 3, 4, 5, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 7, 27, 4, 5, 6, TimeSpan.FromHours(8)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.StartsWith("/robot/rawMsg/list?", handler.PathAndQuery);
        Assert.Contains("robotId=robot-7", handler.PathAndQuery);
        Assert.Contains("page=1", handler.PathAndQuery);
        Assert.Contains("size=10", handler.PathAndQuery);
        Assert.Contains("sort=run_time%2Cdesc", handler.PathAndQuery);
        Assert.DoesNotContain("desc=true", handler.PathAndQuery);
        Assert.Contains("startTime=2026-07-26%2019%3A04%3A05", handler.PathAndQuery);
        Assert.Contains("endTime=2026-07-26%2020%3A05%3A06", handler.PathAndQuery);
        Assert.Contains("type=512", handler.PathAndQuery);
        Assert.Contains("messageId=member%20command%2F1", handler.PathAndQuery);
        var result = Assert.Single(results);
        Assert.Equal(512, result.Type);
        Assert.Equal("member-command-1", result.MessageId);
        Assert.Equal("[\"opaque\"]", result.SuccessListRaw);
        Assert.DoesNotContain("must-not-be-returned", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Query_rejects_blank_message_id()
    {
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.OK, "{}"))
        {
            BaseAddress = new Uri("https://fake.worktool.test/")
        };
        var sut = new WorkToolClient(http, new FixedCredentials());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.ListGroupMemberSnapshotResultsAsync(
                Guid.NewGuid(),
                " ",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Query_returns_empty_for_documented_empty_data()
    {
        using var http = new HttpClient(new RecordingHandler(
            HttpStatusCode.OK,
            """{"code":200,"message":"success","data":[]}"""))
        {
            BaseAddress = new Uri("https://fake.worktool.test/")
        };
        var sut = new WorkToolClient(http, new FixedCredentials());

        var results = await sut.ListGroupMemberSnapshotResultsAsync(
            Guid.NewGuid(),
            "message-1",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, """{"code":200,"data":[]}""", "worktool_http_502")]
    [InlineData(HttpStatusCode.OK, """{"code":500,"data":[]}""", "worktool_code_500")]
    [InlineData(HttpStatusCode.OK, "not-json", "worktool_invalid_response")]
    public async Task Query_returns_a_typed_failure_for_invalid_responses(
        HttpStatusCode status,
        string body,
        string expectedCode)
    {
        using var http = new HttpClient(new RecordingHandler(status, body))
        {
            BaseAddress = new Uri("https://fake.worktool.test/")
        };
        var sut = new WorkToolClient(http, new FixedCredentials());

        var exception = await Assert.ThrowsAsync<WorkToolRawResultException>(() =>
            sut.ListGroupMemberSnapshotResultsAsync(
                Guid.NewGuid(),
                "message-1",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.FailureCode);
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string PathAndQuery { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            PathAndQuery = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class FixedCredentials : IWorkToolCredentialResolver
    {
        public Task<string> ResolveRobotIdAsync(
            Guid robotConfigId,
            CancellationToken cancellationToken) =>
            Task.FromResult("robot-7");
    }
}
