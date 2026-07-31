using WechatRobot.Application.Conversations;

namespace WechatRobot.UnitTests.Conversations;

public sealed class AnswerOutputFirewallTests
{
    public static TheoryData<string> GenericMarkers => new()
    {
        "Warranty is two years [1]",
        "参考：manual.pdf",
        "Source: handbook.docx",
        "See page 12 for details",
        "详情：https://example.test/manual",
        "来源：产品手册",
        "References: internal knowledge"
    };

    [Theory]
    [MemberData(nameof(GenericMarkers))]
    public void Generic_source_markers_are_rejected(string output)
    {
        Assert.False(new AnswerOutputFirewall().Validate(output, [Evidence()]).IsSafe);
    }

    [Theory]
    [InlineData("The warranty is two years, according to the policy.")]
    [InlineData("质保期为两年，如需维修请联系售后。")]
    public void Clean_plain_answers_are_accepted(string output)
    {
        Assert.True(new AnswerOutputFirewall().Validate(output, [Evidence()]).IsSafe);
    }

    [Fact]
    public void Exact_evidence_document_marker_is_rejected_even_without_source_prefix()
    {
        Assert.False(new AnswerOutputFirewall().Validate("Please read manual.pdf", [Evidence()]).IsSafe);
    }

    [Fact]
    public void Exact_evidence_filename_and_url_are_rejected()
    {
        var evidence = Evidence() with { DocumentTitle = "Warranty", SourceFileName = "private-handbook.bin", SourceUri = "oss://bucket/private-object" };
        var firewall = new AnswerOutputFirewall();

        Assert.False(firewall.Validate("See private-handbook.bin", [evidence]).IsSafe);
        Assert.False(firewall.Validate("See oss://bucket/private-object", [evidence]).IsSafe);
    }

    [Fact]
    public void Every_internal_evidence_id_format_is_rejected()
    {
        var evidence = Evidence();
        var firewall = new AnswerOutputFirewall();

        foreach (var id in new[] { evidence.DocumentId, evidence.VersionId, evidence.ChunkId })
        {
            Assert.False(firewall.Validate($"internal {id:D}", [evidence]).IsSafe);
            Assert.False(firewall.Validate($"internal {id:N}".ToUpperInvariant(), [evidence]).IsSafe);
        }
    }

    public static TheoryData<string> InternalToolProtocolMarkers => new()
    {
        """
        ``` <|tool_call|>
        [{"name":"web_search","arguments":{"query":"法国商务五年签证 说明信 怎么写 模板"}}]
        <|tool_call|>
        ```
        """,
        "<|tool_response|>{\"results\":[]}",
        "{\"function_call\":{\"name\":\"web_search\"}}",
        "{\"tool_calls\":[{\"name\":\"web_search\"}]}"
    };

    [Theory]
    [MemberData(nameof(InternalToolProtocolMarkers))]
    public void Internal_tool_protocol_markers_are_rejected_from_every_model_output(
        string output)
    {
        var firewall = new AnswerOutputFirewall();

        Assert.False(firewall.Validate(output, [Evidence()]).IsSafe);
        Assert.False(firewall.ValidateUngrounded(output).IsSafe);
    }

    [Fact]
    public void Markerless_web_search_tool_arguments_are_rejected()
    {
        const string output =
            """[{"name":"web_search","arguments":{"query":"法国商务五年签证说明信"}}]""";
        var firewall = new AnswerOutputFirewall();

        Assert.False(firewall.Validate(output, [Evidence()]).IsSafe);
        Assert.False(firewall.ValidateUngrounded(output).IsSafe);
    }

    [Fact]
    public void Normal_answer_that_mentions_search_as_a_user_concept_is_allowed()
    {
        var firewall = new AnswerOutputFirewall();

        Assert.True(firewall.ValidateUngrounded(
            "您可以在法国签证官网查询最新材料清单。").IsSafe);
    }

    private static RetrievalEvidence Evidence() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4, .9, [], "manual.pdf", "safe evidence");
}
