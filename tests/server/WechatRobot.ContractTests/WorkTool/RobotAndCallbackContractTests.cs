using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class RobotAndCallbackContractTests
{
    [Fact]
    public async Task GetRobotAsync_uses_official_path_and_accepts_documented_code_200()
    {
        using var handler = new CapturingHandler(
            """{"code":200,"message":"操作成功","data":{"robotId":"robot-7","openCallback":1,"replyAll":1}}""");
        var sut = Client(handler);

        var result = await sut.GetRobotAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.True(result.Reachable);
        Assert.True(result.MessageCallbackEnabled);
        Assert.True(result.ReplyAllEnabled);
        Assert.Equal("/robot/robotInfo/get?robotId=robot-7", handler.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetOnlineAsync_does_not_invent_false_when_official_response_has_no_status()
    {
        using var handler = new CapturingHandler("""{"code":200,"message":"操作成功","data":{}}""");

        var result = await Client(handler).GetOnlineAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.Null(result.Online);
        Assert.Equal("/robot/robotInfo/online?robotId=robot-7", handler.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("""{"code":200,"data":{"online":true,"status":1}}""")]
    [InlineData("""{"unexpected":"successful-but-undocumented"}""")]
    public async Task GetOnlineAsync_treats_every_http_2xx_body_as_unknown_without_failure(
        string body)
    {
        using var handler = new CapturingHandler(body);

        var result = await Client(handler).GetOnlineAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.Null(result.Online);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public async Task ConfigureMessageCallbackAsync_uses_robot_update_contract()
    {
        using var handler = new CapturingHandler("""{"code":0,"message":"ok","data":null}""");
        var sut = Client(handler);

        var result = await sut.ConfigureMessageCallbackAsync(
            Guid.NewGuid(),
            new WorkToolMessageCallbackRequest(
                true,
                true,
                new Uri("https://robot.example/api/worktool/callback/route?token=fake-secret")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Configured);
        Assert.Equal("/robot/robotInfo/update?robotId=robot-7", handler.RequestUri!.PathAndQuery);
        Assert.Equal(
            JsonNode.Parse(
                """{"openCallback":1,"replyAll":1,"callbackUrl":"https://robot.example/api/worktool/callback/route?token=fake-secret"}""")!
                .ToJsonString(),
            JsonNode.Parse(handler.Body)!.ToJsonString());
        Assert.Equal(Encoding.UTF8.GetByteCount(handler.Body), handler.ContentLength);
    }

    [Fact]
    public async Task ListEventCallbacksAsync_uses_official_query_contract()
    {
        using var handler = new CapturingHandler(
            """{"code":0,"message":"ok","data":[{"id":7,"type":1,"callBackUrl":"https://robot.example/results","typeName":"指令执行结果"}]}""");

        var result = await Client(handler).ListEventCallbacksAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        var callback = Assert.Single(result);
        Assert.Equal(1, callback.Type);
        Assert.Equal("https://robot.example/results", callback.CallbackUrl);
        Assert.Equal("/robot/robotInfo/callBack/get?robotId=robot-7&robotKey=", handler.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task DeleteEventCallbackAsync_uses_official_delete_contract()
    {
        using var handler = new CapturingHandler("""{"code":0,"message":"ok","data":null}""");

        var result = await Client(handler).DeleteEventCallbackAsync(
            Guid.NewGuid(),
            1,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("/robot/robotInfo/callBack/deleteByType?robotId=robot-7", handler.RequestUri!.PathAndQuery);
        Assert.Equal(
            JsonNode.Parse("""{"type":1}""")!.ToJsonString(),
            JsonNode.Parse(handler.Body)!.ToJsonString());
    }

    [Fact]
    public async Task Event_callback_failure_does_not_return_secret_bearing_url()
    {
        using var handler = new CapturingHandler(
            """{"code":1001,"message":"rejected https://robot.example/results?token=fake-secret","data":null}""");

        var result = await Client(handler).BindEventCallbackAsync(
            Guid.NewGuid(),
            1,
            new Uri("https://robot.example/results?token=fake-secret"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("worktool_code_1001", result.FailureCode);
        Assert.DoesNotContain("fake-secret", result.FailureCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Robot_probe_retries_one_transport_failure_with_fresh_http_11_connections()
    {
        using var handler = new TransportFailureThenSuccessHandler(
            """{"code":200,"message":"操作成功","data":{"robotId":"robot-7","openCallback":1,"replyAll":1}}""");

        var result = await Client(handler).GetRobotAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Reachable);
        Assert.Equal(2, handler.CallCount);
        Assert.All(handler.Versions, version => Assert.Equal(HttpVersion.Version11, version));
        Assert.All(handler.ConnectionCloseValues, Assert.True);
    }

    [Fact]
    public async Task Message_callback_configuration_retries_one_transport_failure()
    {
        using var handler = new TransportFailureThenSuccessHandler(
            """{"code":0,"message":"ok","data":null}""");

        var result = await Client(handler).ConfigureMessageCallbackAsync(
            Guid.NewGuid(),
            new WorkToolMessageCallbackRequest(
                true,
                true,
                new Uri("https://robot.example/api/worktool/callback/route")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Configured);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Message_delivery_is_not_retried_after_a_transport_failure()
    {
        using var handler = new TransportFailureThenSuccessHandler(
            """{"code":0,"message":"ok","data":"message-7"}""");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(handler).SendTextAsync(
                new WorkToolSendRequest(
                    Guid.NewGuid(),
                    "测试群",
                    "只发送一次",
                    "idempotency-7"),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.CallCount);
    }

    private static WorkToolClient Client(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.worktool.test/") },
            new FixedCredentials());

    private sealed class CapturingHandler(string body) : HttpMessageHandler, IDisposable
    {
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ContentLength = request.Content?.Headers.ContentLength;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TransportFailureThenSuccessHandler(string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<Version> Versions { get; } = [];
        public List<bool> ConnectionCloseValues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Versions.Add(request.Version);
            ConnectionCloseValues.Add(request.Headers.ConnectionClose == true);
            if (CallCount == 1)
                throw new HttpRequestException("Simulated Windows TLS decryption failure.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
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
