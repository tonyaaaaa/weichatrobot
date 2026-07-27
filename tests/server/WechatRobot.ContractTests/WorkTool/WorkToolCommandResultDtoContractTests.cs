using WechatRobot.Application.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class WorkToolCommandResultDtoContractTests
{
    [Theory]
    [InlineData(203)]
    [InlineData(206)]
    [InlineData(207)]
    public void Actual_command_types_are_accepted(int commandType)
    {
        var result = new WorkToolCommandResultDto
        {
            MessageId = "message-1",
            ErrorCode = 0,
            Type = commandType
        };

        Assert.True(result.IsValid(out var reason), reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    public void Callback_registration_and_unknown_types_are_rejected(int commandType)
    {
        var result = new WorkToolCommandResultDto
        {
            MessageId = "message-1",
            ErrorCode = 0,
            Type = commandType
        };

        Assert.False(result.IsValid(out var reason));
        Assert.Equal("unsupported-result-type", reason);
    }
}
