using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class GroupOperationsContractTests
{
    [Fact]
    public async Task Create_external_group_maps_to_command_206()
    {
        using var handler = new CapturingHandler("{\"code\":0,\"message\":\"ok\"}");
        var sut = new WorkToolClient(new HttpClient(handler) { BaseAddress = new Uri("https://fake.worktool.test/") }, new FixedCredentials());

        var result = await sut.ExecuteGroupOperationAsync(new WorkToolGroupOperationRequest(Guid.NewGuid(), WorkToolGroupOperationKind.Create, "技术部", ["customer-1", "employee-1"], "欢迎来到技术部"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("/wework/sendRawMessage?robotId=robot-7", handler.RequestUri!.PathAndQuery);
        Assert.Contains("\"type\":206", handler.Body, StringComparison.Ordinal);
        Assert.Equal(JsonNode.Parse("""{"socketType":2,"list":[{"type":206,"groupName":"技术部","selectList":["customer-1","employee-1"],"groupAnnouncement":"欢迎来到技术部"}]}""")!.ToJsonString(), JsonNode.Parse(handler.Body)!.ToJsonString());
    }

    [Theory]
    [InlineData(WorkToolGroupOperationKind.AddMembers, """{"socketType":2,"list":[{"type":207,"groupName":"group-name","newGroupName":null,"newGroupAnnouncement":null,"selectList":["member-1"],"showMessageHistory":false,"removeList":[]}]}""")]
    [InlineData(WorkToolGroupOperationKind.RemoveMembers, """{"socketType":2,"list":[{"type":207,"groupName":"group-name","newGroupName":null,"newGroupAnnouncement":null,"selectList":[],"showMessageHistory":false,"removeList":["member-1"]}]}""")]
    [InlineData(WorkToolGroupOperationKind.Rename, """{"socketType":2,"list":[{"type":207,"groupName":"group-name","newGroupName":"new value","newGroupAnnouncement":null,"selectList":[],"showMessageHistory":false,"removeList":[]}]}""")]
    [InlineData(WorkToolGroupOperationKind.UpdateAnnouncement, """{"socketType":2,"list":[{"type":207,"groupName":"group-name","newGroupName":null,"newGroupAnnouncement":"new value","selectList":[],"showMessageHistory":false,"removeList":[]}]}""")]
    public async Task Existing_group_operations_map_to_documented_command_207(WorkToolGroupOperationKind kind, string expected)
    {
        using var handler = new CapturingHandler("{\"code\":0}");
        var sut = new WorkToolClient(new HttpClient(handler) { BaseAddress = new Uri("https://fake.worktool.test/") }, new FixedCredentials());

        var result = await sut.ExecuteGroupOperationAsync(new WorkToolGroupOperationRequest(Guid.NewGuid(), kind, "group-name", ["member-1"], "new value"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(JsonNode.Parse(expected)!.ToJsonString(), JsonNode.Parse(handler.Body)!.ToJsonString());
    }

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FixedCredentials : IWorkToolCredentialResolver
    {
        public Task<string> ResolveRobotIdAsync(Guid robotConfigId, CancellationToken cancellationToken) =>
            Task.FromResult("robot-7");
    }
}
