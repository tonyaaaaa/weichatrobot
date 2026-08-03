using WechatRobot.LegacyVisaImport;
using System.Text;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge.Parsing;

namespace WechatRobot.LegacyVisaImportTests;

public sealed class LegacyVisaMarkdownRendererTests
{
    [Fact]
    public void Render_creates_stable_searchable_markdown()
    {
        var product = Product([
            new("worklist", [new("护照", "原件", true, "有效期至少六个月", "https://example/template", null)])
        ]);

        var rendered = LegacyVisaMarkdownRenderer.Render(
            product,
            new DateOnly(2026, 8, 3));

        Assert.Equal("legacy-visa-123-日本三年多次签证.md", rendered.FileName);
        Assert.Contains("# 日本三年多次签证", rendered.Markdown);
        Assert.Contains("签证名称：日本三年多次签证", rendered.Markdown);
        Assert.Contains("国家名称：日本", rendered.Markdown);
        Assert.DoesNotContain("旧系统签证 ID：", rendered.Markdown);
        Assert.DoesNotContain("国家 ID：", rendered.Markdown);
        Assert.Contains("申请人类型：在职人员（原始代码：worklist）", rendered.Markdown);
        Assert.Contains("## 在职人员（worklist）", rendered.Markdown);
        Assert.Contains("### 护照", rendered.Markdown);
        Assert.Contains("材料名称：护照", rendered.Markdown);
        Assert.DoesNotContain("旧系统参考价格", rendered.Markdown);
        Assert.DoesNotContain("价格说明", rendered.Markdown);
        Assert.Equal(64, rendered.Sha256.Length);
    }

    [Fact]
    public void Normalize_splits_applicant_codes_and_deduplicates_exact_materials()
    {
        var material = new LegacyMaterialRequirement("护照", "原件", true, null, null, null);
        var sets = LegacyVisaNormalizer.GroupMaterials([
            new("worklist, studentlist;worklist", material),
            new("worklist", material)
        ]);

        Assert.Equal(["studentlist", "worklist"], sets.Select(x => x.ApplicantTypeCode));
        Assert.All(sets, set => Assert.Single(set.Materials));
    }

    [Fact]
    public void Render_omits_blank_optional_fields_and_escapes_untrusted_structure()
    {
        var product = Product([
            new("alllist", [new("# 银行<流水>", "", false, "<p>第一行</p><p>第二行&nbsp;</p>", null, null)])
        ]) with { AreaRule = null, VisaCenter = "" };

        var rendered = LegacyVisaMarkdownRenderer.Render(product, new DateOnly(2026, 8, 3));

        Assert.DoesNotContain("受理范围：", rendered.Markdown);
        Assert.DoesNotContain("签证中心：", rendered.Markdown);
        Assert.Contains("### \\# 银行&lt;流水&gt;", rendered.Markdown);
        Assert.Contains("第一行 第二行", rendered.Markdown);
        Assert.DoesNotContain("&lt;p&gt;", rendered.Markdown);
    }

    [Fact]
    public void Render_uses_verified_legacy_applicant_labels()
    {
        var material = new LegacyMaterialRequirement("护照", null, true, null, null, null);
        var product = Product([
            new("finishlist", [material]),
            new("studentlist", [material]),
            new("student2list", [material]),
            new("freelist", [material]),
            new("childlist", [material])
        ]);

        var markdown = LegacyVisaMarkdownRenderer.Render(product, new DateOnly(2026, 8, 3)).Markdown;

        Assert.Contains("退休人员（原始代码：finishlist）", markdown);
        Assert.Contains("18岁以上学生（原始代码：studentlist）", markdown);
        Assert.Contains("18岁以下学生（原始代码：student2list）", markdown);
        Assert.Contains("无业人员（原始代码：freelist）", markdown);
        Assert.Contains("学龄前儿童（原始代码：childlist）", markdown);
    }

    [Fact]
    public async Task Render_keeps_material_name_in_actual_chunk_text()
    {
        var product = Product([
            new("worklist", [new("护照", "原件", true, "有效期六个月", null, null)])
        ]);
        var rendered = LegacyVisaMarkdownRenderer.Render(product, new DateOnly(2026, 8, 3));
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(rendered.Markdown));
        using var context = new DocumentProcessingContext(
            new DocumentParsingLimits(1_000_000, 10, 10_000_000, TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        var parsed = await new MarkdownTextParser().ParseAsync(source, "text/markdown", context);
        var chunks = new ChunkingService().Generate(parsed.Blocks, new ChunkPolicy(ChunkPolicyKind.Smart));

        var materialChunk = Assert.Single(chunks, chunk =>
            chunk.Text.Contains("材料名称：护照", StringComparison.Ordinal));
        Assert.StartsWith(
            "标题路径：日本三年多次签证 > 在职人员（worklist） > 护照\n",
            materialChunk.Text,
            StringComparison.Ordinal);
        Assert.Contains("材料名称：护照", materialChunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_repeats_complete_material_context_when_long_instructions_split()
    {
        var product = Product([
            new("worklist", [new("护照", "原件", true, new string('甲', 1_600), null, null)])
        ]);
        var rendered = LegacyVisaMarkdownRenderer.Render(product, new DateOnly(2026, 8, 3));
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(rendered.Markdown));
        using var context = new DocumentProcessingContext(
            new DocumentParsingLimits(1_000_000, 10, 10_000_000, TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        var parsed = await new MarkdownTextParser().ParseAsync(source, "text/markdown", context);
        var chunks = new ChunkingService().Generate(
            parsed.Blocks,
            new ChunkPolicy(ChunkPolicyKind.Smart, TargetTokens: 120, OverlapTokens: 20, MaximumTokens: 150));
        var materialChunks = chunks.Where(chunk => chunk.Headings.LastOrDefault() == "护照").ToArray();

        Assert.True(materialChunks.Length > 1);
        Assert.All(materialChunks, chunk =>
        {
            Assert.StartsWith(
                "标题路径：日本三年多次签证 > 在职人员（worklist） > 护照\n",
                chunk.Text,
                StringComparison.Ordinal);
            Assert.InRange(chunk.EstimatedTokens, 1, 150);
        });
    }

    [Fact]
    public void Render_marks_visa_products_without_source_materials()
    {
        var rendered = LegacyVisaMarkdownRenderer.Render(Product([]), new DateOnly(2026, 8, 3));

        Assert.Contains("材料状态：旧系统未配置材料", rendered.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_includes_notice_description_in_indexed_chunk_text()
    {
        var product = Product([]) with
        {
            NoticeDescription = "<p>请勿购买不可退改机票。</p><p>领馆可能要求补充材料。</p>"
        };
        var rendered = LegacyVisaMarkdownRenderer.Render(product, new DateOnly(2026, 8, 3));

        Assert.Contains("## 注意事项", rendered.Markdown, StringComparison.Ordinal);
        Assert.Contains(
            "注意事项：请勿购买不可退改机票。 领馆可能要求补充材料。",
            rendered.Markdown,
            StringComparison.Ordinal);

        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(rendered.Markdown));
        using var context = new DocumentProcessingContext(
            new DocumentParsingLimits(1_000_000, 10, 10_000_000, TimeSpan.FromSeconds(10)),
            CancellationToken.None);
        var parsed = await new MarkdownTextParser().ParseAsync(source, "text/markdown", context);
        var chunks = new ChunkingService().Generate(parsed.Blocks, new ChunkPolicy(ChunkPolicyKind.Smart));

        var noticeChunk = Assert.Single(chunks, chunk =>
            chunk.Text.Contains("注意事项：请勿购买", StringComparison.Ordinal));
        Assert.StartsWith(
            "标题路径：日本三年多次签证 > 注意事项\n",
            noticeChunk.Text,
            StringComparison.Ordinal);
    }

    private static LegacyVisaProduct Product(IReadOnlyList<LegacyApplicantMaterialSet> sets) => new(
        "123",
        "日本三年多次签证",
        "81",
        "全国受理",
        "广州",
        "8-10个工作日",
        "90天",
        1288.50m,
        sets)
    {
        CountryName = "日本"
    };
}
