using System.Net;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class GroupOperationsContractTests
{
    [Fact]
    public async Task Create_external_group_maps_to_command_206()
    {
        using var handler = new CapturingHandler("{\"code\":0,\"message\":\"ok\"}");
        var sut = new WorkToolClient(new HttpClient(handler) { BaseAddress = new Uri("https://fake.worktool.test/") });

        var result = await sut.ExecuteGroupOperationAsync(new WorkToolGroupOperationRequest("robot-7", WorkToolGroupOperationKind.Create, "技术部", ["customer-1", "employee-1"], null), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("/wework/sendRawMessage?robotId=robot-7", handler.RequestUri!.PathAndQuery);
        Assert.Contains("\"type\":206", handler.Body, StringComparison.Ordinal);
        using var command = JsonDocument.Parse(handler.Body);
        Assert.Equal("技术部", command.RootElement.GetProperty("list")[0].GetProperty("groupName").GetString());
    }

    [Theory]
    [InlineData(WorkToolGroupOperationKind.AddMembers, "addMembers")]
    [InlineData(WorkToolGroupOperationKind.RemoveMembers, "removeMembers")]
    [InlineData(WorkToolGroupOperationKind.Rename, "rename")]
    [InlineData(WorkToolGroupOperationKind.UpdateAnnouncement, "updateAnnouncement")]
    [InlineData(WorkToolGroupOperationKind.UpdateRemark, "updateRemark")]
    public async Task Existing_group_operations_map_to_command_207(WorkToolGroupOperationKind kind, string action)
    {
        using var handler = new CapturingHandler("{\"code\":0}");
        var sut = new WorkToolClient(new HttpClient(handler) { BaseAddress = new Uri("https://fake.worktool.test/") });

        var result = await sut.ExecuteGroupOperationAsync(new WorkToolGroupOperationRequest("robot-7", kind, "group-external-id", ["member-1"], "new value"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains("\"type\":207", handler.Body, StringComparison.Ordinal);
        Assert.Contains($"\"action\":\"{action}\"", handler.Body, StringComparison.Ordinal);
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
}
