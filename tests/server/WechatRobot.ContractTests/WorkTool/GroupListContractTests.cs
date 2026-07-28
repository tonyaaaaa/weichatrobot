using System.Net;
using System.Reflection;
using System.Text;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class GroupListContractTests
{
    [Fact]
    public async Task ListGroupsAsync_maps_only_documented_non_identity_fields()
    {
        using var handler = new CapturingHandler(
            """
            {
              "code": 0,
              "message": "success",
              "data": {
                "pageNum": 1,
                "pageSize": 50,
                "totalPage": 1,
                "total": 1,
                "list": [{
                  "workType": "external",
                  "groupName": "Support",
                  "masterName": "成员甲",
                  "robotId": "must-not-leak",
                  "msgInsertTime": "2026-07-27 12:00:00",
                  "msgNum": 7,
                  "membersNum": 12,
                  "groupAnnouncement": "服务公告",
                  "parentId": "must-not-leak",
                  "level": 1,
                  "createTime": "2026-07-01 10:00:00",
                  "updateTime": "2026-07-27 12:00:00"
                }]
              }
            }
            """);
        var sut = Client(handler);

        var result = await sut.ListGroupsAsync(
            Guid.NewGuid(),
            "Support",
            1,
            50,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "/robot/wework/group/list?robotId=robot-7&groupName=Support&page=1&size=50",
            handler.RequestUri!.PathAndQuery);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(50, result.PageSize);
        Assert.Equal(1, result.TotalPages);
        Assert.Equal(1, result.Total);
        var group = Assert.Single(result.Items);
        Assert.Equal("Support", group.GroupName);
        Assert.Equal("成员甲", group.MasterName);
        Assert.Equal(12, group.MembersCount);
        Assert.Equal("服务公告", group.GroupAnnouncement);
        Assert.DoesNotContain(
            typeof(WorkToolGroupSummary).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => property.Name.Contains("RobotId", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("ParentId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListGroupsAsync_accepts_observed_success_code_200()
    {
        using var handler = new CapturingHandler(
            """
            {
              "code": 200,
              "message": "操作成功",
              "data": {
                "pageNum": 1,
                "pageSize": 50,
                "totalPage": 1,
                "total": 1,
                "list": [{
                  "groupName": "Support",
                  "masterName": "成员甲",
                  "membersNum": 12,
                  "groupAnnouncement": "服务公告"
                }]
              }
            }
            """);

        var result = await Client(handler).ListGroupsAsync(
            Guid.NewGuid(),
            null,
            1,
            50,
            TestContext.Current.CancellationToken);

        var group = Assert.Single(result.Items);
        Assert.Equal("Support", group.GroupName);
        Assert.Equal(1, result.Total);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task ListGroupsAsync_rejects_out_of_range_pagination(int page, int pageSize)
    {
        using var handler = new CapturingHandler("""{"code":0,"data":{"list":[]}}""");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Client(handler).ListGroupsAsync(
                Guid.NewGuid(),
                null,
                page,
                pageSize,
                TestContext.Current.CancellationToken));
    }

    private static WorkToolClient Client(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.worktool.test/") },
            new FixedCredentials());

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
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
