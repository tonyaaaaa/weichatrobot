using WechatRobot.LegacyVisaImport;

namespace WechatRobot.LegacyVisaImportTests;

public sealed class LegacyVisaImportPlannerTests
{
    [Fact]
    public void ResolveTag_requires_one_exact_enabled_tag()
    {
        var id = Guid.NewGuid();
        var tags = new[]
        {
            new KnowledgeTagOption(id, "签证知识", false),
            new KnowledgeTagOption(Guid.NewGuid(), "其他", false)
        };

        Assert.Equal(id, LegacyVisaImportPlanner.ResolveTag(tags, "签证知识"));
        Assert.Throws<InvalidOperationException>(() =>
            LegacyVisaImportPlanner.ResolveTag(tags, "签证"));
    }

    [Theory]
    [InlineData(true, true, "skip")]
    [InlineData(true, false, "update")]
    [InlineData(false, false, "create")]
    public void Decide_is_idempotent_and_uses_exact_filename(
        bool matchingDocumentExists,
        bool checkpointHashMatches,
        string expected)
    {
        var rendered = new RenderedVisaDocument("legacy-visa-123-日本.md", "body", "abc");
        var documents = matchingDocumentExists
            ? new[] { new KnowledgeDocumentMatch(Guid.NewGuid(), rendered.FileName) }
            : Array.Empty<KnowledgeDocumentMatch>();
        var checkpoint = checkpointHashMatches
            ? new LegacyImportCheckpointEntry("abc", documents.Single().Id, Guid.NewGuid(), "consistent")
            : null;

        Assert.Equal(expected, LegacyVisaImportPlanner.Decide(rendered, documents, checkpoint).Action);
    }

    [Theory]
    [InlineData("uploaded", "resume-uploaded")]
    [InlineData("approved", "resume-approved")]
    [InlineData("indexing", "resume-indexing")]
    public void Decide_resumes_matching_interrupted_checkpoint(
        string checkpointState,
        string expectedAction)
    {
        var documentId = Guid.NewGuid();
        var rendered = new RenderedVisaDocument("legacy-visa-123-日本.md", "body", "abc");
        var checkpoint = new LegacyImportCheckpointEntry(
            "abc", documentId, Guid.NewGuid(), checkpointState);

        var decision = LegacyVisaImportPlanner.Decide(
            rendered,
            [new KnowledgeDocumentMatch(documentId, rendered.FileName)],
            checkpoint);

        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(documentId, decision.DocumentId);
    }

    [Fact]
    public void Decide_creates_when_only_matching_document_is_deleted()
    {
        var rendered = new RenderedVisaDocument("legacy-visa-123-日本.md", "body", "abc");
        var documents = new[]
        {
            new KnowledgeDocumentMatch(Guid.NewGuid(), rendered.FileName, "deleted")
        };

        var decision = LegacyVisaImportPlanner.Decide(rendered, documents, null);

        Assert.Equal("create", decision.Action);
        Assert.Null(decision.DocumentId);
    }

    [Theory]
    [InlineData("uploading", "uploaded")]
    [InlineData("uploaded", "uploaded")]
    [InlineData("preview", "uploaded")]
    [InlineData("failed", "failed")]
    [InlineData("approved", "approved")]
    [InlineData("indexing", "indexing")]
    [InlineData("active", "indexing")]
    public void ResumeState_maps_authoritative_version_status(
        string versionStatus,
        string expectedCheckpointState)
    {
        Assert.Equal(expectedCheckpointState,
            LegacyVisaImportPlanner.ResumeState(versionStatus));
    }

    [Fact]
    public void ResolveSourceDuplicate_reuses_the_first_same_content_checkpoint()
    {
        var canonical = new LegacyImportCheckpointEntry(
            "abc", Guid.NewGuid(), Guid.NewGuid(), "indexing");
        var resolvedBySha = new Dictionary<string, LegacyImportCheckpointEntry>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["abc"] = canonical
        };

        var resolved = LegacyVisaImportPlanner.ResolveSourceDuplicate(
            new RenderedVisaDocument("second-name.md", "same body", "ABC"),
            resolvedBySha);

        Assert.Same(canonical, resolved);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Index_recovery_is_bounded(int attempts, bool expected)
    {
        Assert.Equal(expected, LegacyVisaImportPlanner.CanRetryIndex(attempts));
    }
}
