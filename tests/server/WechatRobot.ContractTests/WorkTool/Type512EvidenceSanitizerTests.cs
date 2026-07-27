using System.Text.Json;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public sealed class Type512EvidenceSanitizerTests
{
    [Fact]
    public void Shape_reports_json_kinds_and_sorted_property_names_without_values()
    {
        const string nickname = "绝不能出现在证据中的昵称";
        const string groupName = "绝不能出现在证据中的群名";
        var result = new WorkToolRawCommandResult(
            $$"""{"socketType":2,"list":[{"type":512,"groupName":"{{groupName}}"}]}""",
            1,
            "绝不能出现在证据中的错误",
            "2026-07-27 12:00:00",
            1,
            512,
            "绝不能出现在证据中的消息ID",
            $$"""[{"nickname":"{{nickname}}","role":"member"}]""",
            """{"z":[],"a":null,"a":null}""",
            1);

        var shape = Type512EvidenceSanitizer.Create(
            [result],
            "绝不能出现在证据中的消息ID");
        var json = JsonSerializer.Serialize(shape);

        Assert.Equal("Array", shape.SuccessListJsonKind);
        Assert.Equal("Object", shape.FailListJsonKind);
        Assert.Equal(["groupName", "list", "socketType", "type"],
            shape.RawMessagePropertyNames);
        Assert.Equal(["nickname", "role"],
            shape.SuccessListObjectPropertyNames);
        Assert.Equal(["a", "z"], shape.FailListObjectPropertyNames);
        Assert.DoesNotContain(nickname, json);
        Assert.DoesNotContain(groupName, json);
        Assert.DoesNotContain("绝不能出现在证据中的消息ID", json);
        Assert.DoesNotContain("绝不能出现在证据中的错误", json);
    }

    [Fact]
    public void Shape_reports_invalid_json_without_copying_raw_input()
    {
        const string invalid = "invalid-secret-nickname";
        var result = new WorkToolRawCommandResult(
            null,
            0,
            null,
            null,
            1,
            512,
            "message-1",
            invalid,
            "null",
            null);

        var shape = Type512EvidenceSanitizer.Create([result], "message-1");
        var json = JsonSerializer.Serialize(shape);

        Assert.Equal("InvalidJson", shape.SuccessListJsonKind);
        Assert.Equal("Null", shape.FailListJsonKind);
        Assert.DoesNotContain(invalid, json);
    }

    [Theory]
    [InlineData("\"opaque\"", "String")]
    [InlineData("[]", "Array")]
    [InlineData("{}", "Object")]
    [InlineData("null", "Null")]
    public void Shape_reports_each_documented_json_kind(
        string raw,
        string expectedKind)
    {
        var result = new WorkToolRawCommandResult(
            null,
            1,
            null,
            null,
            1,
            512,
            "message-1",
            raw,
            raw,
            null);

        var shape = Type512EvidenceSanitizer.Create([result], "message-1");

        Assert.Equal(expectedKind, shape.SuccessListJsonKind);
        Assert.Equal(expectedKind, shape.FailListJsonKind);
    }
}
