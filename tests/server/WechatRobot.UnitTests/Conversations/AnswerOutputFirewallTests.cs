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

    private static RetrievalEvidence Evidence() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4, .9, [], "manual.pdf", "safe evidence");
}
