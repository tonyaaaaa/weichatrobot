using System.Text.Json;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class CommandResultCallbackContractTests
{
    [Fact]
    public void Documented_type_203_result_is_accepted_without_retaining_raw_message()
    {
        var dto = JsonSerializer.Deserialize<WorkToolCommandResultDto>(
            """
            {
              "messageId": "command-001",
              "errorCode": 0,
              "errorReason": "",
              "runTime": 1721781000000,
              "timeCost": 1.25,
              "type": 203,
              "successList": ["Alice"],
              "failList": [],
              "rawMsg": "must-not-be-retained"
            }
            """);

        Assert.NotNull(dto);
        Assert.True(dto.IsValid(out var reason), reason);
        Assert.Equal("command-001", dto.MessageId);
        Assert.DoesNotContain(dto.GetType().GetProperties(), property =>
            property.Name.Equals("RawMsg", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(InvalidResults))]
    public void Invalid_result_shapes_are_rejected(WorkToolCommandResultDto dto, string expectedReason)
    {
        Assert.False(dto.IsValid(out var reason));
        Assert.Equal(expectedReason, reason);
    }

    public static TheoryData<WorkToolCommandResultDto, string> InvalidResults => new()
    {
        { new() { ErrorCode = 0, Type = 203 }, "missing-message-id" },
        { new() { MessageId = new string('x', 129), ErrorCode = 0, Type = 203 }, "message-id-too-large" },
        { new() { MessageId = "command", Type = 203 }, "missing-result-code" },
        { new() { MessageId = "command", ErrorCode = 0, Type = 1 }, "unsupported-result-type" },
        { new() { MessageId = "command", ErrorCode = 0, Type = 203, SuccessList = Enumerable.Repeat("Alice", 101).ToArray() }, "result-list-too-large" },
        { new() { MessageId = "command", ErrorCode = 0, Type = 203, SuccessList = Enumerable.Repeat("Alice", 51).ToArray(), FailList = Enumerable.Repeat("Bob", 50).ToArray() }, "result-list-too-large" },
        { new() { MessageId = "command", ErrorCode = 0, Type = 203, FailList = [new string('x', 129)] }, "result-name-too-large" }
    };
}
