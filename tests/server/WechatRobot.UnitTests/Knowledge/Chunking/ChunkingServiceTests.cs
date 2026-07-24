using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.UnitTests.Knowledge.Chunking;

public sealed class ChunkingServiceTests
{
    [Fact]
    public void Smart_policy_honors_boundaries_maximum_and_overlap_with_metadata()
    {
        var text = string.Join(' ', Enumerable.Range(1, 80).Select(i => $"word{i}"));
        var blocks = new[] { new ParsedBlock(text, 3, ["章节"], false, null, null) };
        var chunks = new ChunkingService().Generate(blocks, new ChunkPolicy(ChunkPolicyKind.Smart, TargetTokens: 20, OverlapTokens: 5, MaximumTokens: 24));
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => { Assert.InRange(chunk.EstimatedTokens, 1, 24); Assert.Equal(3, chunk.PageNumber); Assert.Equal(["章节"], chunk.Headings); });
        Assert.Contains(chunks[0].Text.Split(' ').TakeLast(5).First(), chunks[1].Text);
    }

    [Theory]
    [InlineData(ChunkPolicyKind.Separator, "--", null)]
    [InlineData(ChunkPolicyKind.Regex, null, "\\|+")]
    public void Advanced_policies_split_on_custom_boundaries(ChunkPolicyKind kind, string? separator, string? pattern)
    {
        var chunks = new ChunkingService().Generate([new ParsedBlock("甲--乙|||丙", 1, [], false, null, null)],
            new ChunkPolicy(kind, 800, 0, 900, separator, pattern));
        Assert.True(chunks.Count >= 2);
        if (kind == ChunkPolicyKind.Regex) Assert.DoesNotContain(chunks, chunk => chunk.Text.Contains("|||", StringComparison.Ordinal));
        else Assert.Contains(chunks, chunk => chunk.Text == "乙|||丙");
    }

    [Fact]
    public void Smart_policy_counts_cjk_without_inserting_spaces()
    {
        var chunks = new ChunkingService().Generate([new ParsedBlock("这是连续中文文本", null, [], false, null, null)],
            new ChunkPolicy(ChunkPolicyKind.Smart, TargetTokens: 4, OverlapTokens: 1, MaximumTokens: 4));
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.DoesNotContain(' ', chunk.Text));
        Assert.All(chunks, chunk => Assert.InRange(chunk.EstimatedTokens, 1, 4));
    }

    [Fact]
    public void Qa_policy_preserves_question_synonyms_and_answer()
    {
        var chunks = new ChunkingService().Generate([], new ChunkPolicy(ChunkPolicyKind.Qa, QaEntries:
            [new QaEntry("怎么退款？", ["退款流程", "如何退费"], "联系售后。")]));
        var chunk = Assert.Single(chunks);
        Assert.Equal("怎么退款？", chunk.Question);
        Assert.Equal(["退款流程", "如何退费"], chunk.Synonyms);
        Assert.Equal("联系售后。", chunk.Answer);
    }

    [Fact]
    public void Preview_mutations_preserve_order_and_metadata()
    {
        var editor = new ChunkPreviewEditor();
        var source = new[]
        {
            new ChunkPreview(Guid.NewGuid(), 0, "甲乙", 2, ["章"], false, null, null),
            new ChunkPreview(Guid.NewGuid(), 1, "丙丁", 3, ["次章"], true, 1, 1)
        };
        var edited = editor.Edit(source, source[0].Id, "甲乙改");
        var split = editor.Split(edited, source[0].Id, 2);
        var merged = editor.Merge(split, split[0].Id, split[1].Id);
        var deleted = editor.Delete(merged, source[1].Id);
        Assert.Equal("甲乙改", Assert.Single(deleted).Text);
        Assert.Equal(2, deleted[0].PageNumber);
        Assert.Equal(["章"], deleted[0].Headings);
        Assert.Equal(0, deleted[0].Sequence);
    }

    [Fact]
    public void Split_then_merge_round_trips_whitespace_exactly()
    {
        var original = new ChunkPreview(Guid.NewGuid(), 0, "alpha  beta\n gamma", 1, ["h"], false, null, null);
        var editor = new ChunkPreviewEditor();
        var split = editor.Split([original], original.Id, 7);
        var merged = editor.Merge(split, split[0].Id, split[1].Id);
        Assert.Equal(original.Text, Assert.Single(merged).Text);
    }

    [Fact]
    public void Edit_preserves_intentional_leading_and_trailing_whitespace()
    {
        var preview = new ChunkPreview(Guid.NewGuid(), 0, "old", null, [], false, null, null);
        var edited = new ChunkPreviewEditor().Edit([preview], preview.Id, "  alpha beta  \n");
        Assert.Equal("  alpha beta  \n", Assert.Single(edited).Text);
    }

    [Fact]
    public void Merge_removes_verified_generated_overlap_once()
    {
        var first = new ChunkPreview(Guid.NewGuid(), 0, "one two three", 1, [], false, null, null);
        var second = new ChunkPreview(Guid.NewGuid(), 1, "two three four", 1, [], false, null, null, OverlapPrefixCharacters: 9);
        var merged = new ChunkPreviewEditor().Merge([first, second], first.Id, second.Id);
        Assert.Equal("one two three four", Assert.Single(merged).Text);
    }

    [Fact]
    public void Qa_merge_rejects_incompatible_metadata_and_round_trips_compatible_split()
    {
        var first = new ChunkPreview(Guid.NewGuid(), 0, "问答 内容", null, [], false, null, null, "问题", ["同义"], "答案");
        var incompatible = first with { Id = Guid.NewGuid(), Sequence = 1, Question = "不同问题" };
        var editor = new ChunkPreviewEditor();
        Assert.Throws<InvalidOperationException>(() => editor.Merge([first, incompatible], first.Id, incompatible.Id));
        var split = editor.Split([first], first.Id, 3);
        Assert.Equal(first.Text, Assert.Single(editor.Merge(split, split[0].Id, split[1].Id)).Text);
    }

    [Fact]
    public void Shared_deadline_and_memory_budget_cover_chunk_output()
    {
        var time = new ManualTimeProvider();
        var timeoutContext = new DocumentProcessingContext(new DocumentParsingLimits(100, 10, 1000, TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken, time, stage => { if (stage == "chunk") time.Advance(TimeSpan.FromSeconds(2)); });
        Assert.Throws<DocumentParsingException>(() => new ChunkingService().Generate([new ParsedBlock("alpha beta", null, [], false, null, null)], new ChunkPolicy(ChunkPolicyKind.Smart, 1, 0, 1), timeoutContext));

        var memoryContext = new DocumentProcessingContext(new DocumentParsingLimits(100, 10, 100, TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);
        memoryContext.Reserve(80, "parsed-text");
        var memory = Assert.Throws<DocumentParsingException>(() => new ChunkingService().Generate([new ParsedBlock("alpha beta gamma", null, [], false, null, null)], new ChunkPolicy(ChunkPolicyKind.Smart, 3, 0, 3), memoryContext));
        Assert.Equal(DocumentParsingError.MemoryLimitExceeded, memory.Error);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
