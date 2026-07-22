using WechatRobot.Application.WorkTool;

namespace WechatRobot.UnitTests.WorkTool;

public sealed class GroupOperationConfirmationTests
{
    [Fact]
    public void Token_is_bound_to_operator_and_normalized_payload()
    {
        var sut = new GroupOperationConfirmationService("test-confirmation-key-which-is-long-enough");
        var now = DateTime.UtcNow;
        var token = sut.Issue("admin-a", "{\"operation\":\"rename\",\"value\":\"new\"}", now, TimeSpan.FromMinutes(2));
        var secondToken = sut.Issue("admin-a", "{\"operation\":\"rename\",\"value\":\"new\"}", now, TimeSpan.FromMinutes(2));

        Assert.NotEqual(token, secondToken);
        Assert.True(sut.IsValid(token, "admin-a", "{\"value\":\"new\",\"operation\":\"rename\"}", now.AddMinutes(1)));
        Assert.False(sut.IsValid(token, "admin-b", "{\"value\":\"new\",\"operation\":\"rename\"}", now.AddMinutes(1)));
        Assert.False(sut.IsValid(token, "admin-a", "{\"operation\":\"rename\",\"value\":\"changed\"}", now.AddMinutes(1)));
        Assert.False(sut.IsValid(token, "admin-a", "{\"value\":\"new\",\"operation\":\"rename\"}", now.AddMinutes(3)));
    }
}
