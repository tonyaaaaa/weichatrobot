using System.Text;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge.Parsing;

namespace WechatRobot.UnitTests.Knowledge.Parsing;

public sealed class DocumentParserTests
{
    [Fact]
    public async Task Text_parser_reads_utf8_and_gb18030_deterministically()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var parser = new MarkdownTextParser();
        await using var utf8Source = File.OpenRead(FixturePath("utf8.txt"));
        var utf8 = await parser.ParseAsync(utf8Source, "text/plain", Context());
        await using var gbSource = File.OpenRead(FixturePath("gb18030.txt"));
        var gb = await parser.ParseAsync(gbSource, "text/plain", Context());
        Assert.Equal("第一行\n第二行", Assert.Single(utf8.Blocks).Text);
        Assert.Equal("中文内容", Assert.Single(gb.Blocks).Text);
        await using var secondSource = File.OpenRead(FixturePath("utf8.txt"));
        var second = await parser.ParseAsync(secondSource, "text/plain", Context());
        Assert.Equal(utf8.Blocks.Select(BlockSignature), second.Blocks.Select(BlockSignature));
    }

    [Fact]
    public async Task Markdown_parser_preserves_heading_hierarchy()
    {
        var parser = new MarkdownTextParser();
        await using var source = File.OpenRead(FixturePath("headings.md"));
        var result = await parser.ParseAsync(source, "text/markdown", Context());
        Assert.Collection(result.Blocks,
            block => { Assert.Equal("介绍", block.Text); Assert.Equal(["产品"], block.Headings); },
            block => { Assert.Equal("步骤", block.Text); Assert.Equal(["产品", "安装"], block.Headings); });
    }

    [Fact]
    public async Task Docx_parser_preserves_headings_and_table_metadata()
    {
        await using var stream = File.OpenRead(FixturePath("headings-table.docx"));
        var result = await new DocxParser().ParseAsync(stream, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Context());
        Assert.Equal("正文", result.Blocks[0].Text);
        Assert.Equal(["指南"], result.Blocks[0].Headings);
        Assert.True(result.Blocks[1].IsTable);
        Assert.Equal(1, result.Blocks[1].TableRows);
        Assert.Equal(2, result.Blocks[1].TableColumns);
    }

    [Fact]
    public async Task Parser_selector_uses_verified_media_type_and_rejects_bad_encoding_and_limits()
    {
        var selector = new DocumentParserSelector([new MarkdownTextParser(), new DocxParser(), new PdfTextParser()]);
        Assert.IsType<MarkdownTextParser>(selector.Select("text/plain"));
        await Assert.ThrowsAsync<DocumentParsingException>(() => selector.Select("text/plain").ParseAsync(
            new MemoryStream([0x81]), "text/plain", Context()));
        await Assert.ThrowsAsync<DocumentParsingException>(() => selector.Select("text/plain").ParseAsync(
            new MemoryStream(new byte[11]), "text/plain", Context(Limits() with { MaximumSourceBytes = 10 })));
    }

    [Fact]
    public async Task Pdf_parser_preserves_pages_and_marks_empty_text_for_future_ocr()
    {
        await using var textPdf = File.OpenRead(FixturePath("text-pages.pdf"));
        var parsed = await new PdfTextParser().ParseAsync(textPdf, "application/pdf", Context());
        Assert.Equal([1, 2], parsed.Blocks.Select(block => block.PageNumber));
        Assert.Equal(["Page one", "Page two"], parsed.Blocks.Select(block => block.Text));
        await using var scanned = File.OpenRead(FixturePath("scanned-empty.pdf"));
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new PdfTextParser().ParseAsync(scanned, "application/pdf", Context()));
        Assert.Equal(DocumentParsingError.EmptyTextPdf, exception.Error);

        await using var tooManyPages = File.OpenRead(FixturePath("text-pages.pdf"));
        var pageLimit = await Assert.ThrowsAsync<DocumentParsingException>(() => new PdfTextParser().ParseAsync(tooManyPages, "application/pdf", Context(Limits() with { MaximumPages = 1 })));
        Assert.Equal(DocumentParsingError.PageLimitExceeded, pageLimit.Error);
    }

    [Fact]
    public async Task Parser_read_honors_execution_timeout()
    {
        var time = new ManualTimeProvider();
        var context = Context(Limits() with { ExecutionTimeout = TimeSpan.FromSeconds(1) }, time, stage => { if (stage == "source-read") time.Advance(TimeSpan.FromSeconds(2)); });
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new MarkdownTextParser().ParseAsync(
            new MemoryStream("text"u8.ToArray()), "text/plain", context));
        Assert.Equal(DocumentParsingError.Timeout, exception.Error);
    }

    [Fact]
    public async Task Docx_parser_rejects_expanded_content_over_memory_limit()
    {
        await using var source = File.OpenRead(FixturePath("headings-table.docx"));
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new DocxParser().ParseAsync(source,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Context(Limits() with { MaximumSourceBytes = 1024, MaximumMemoryBytes = 1100 })));
        Assert.Equal(DocumentParsingError.MemoryLimitExceeded, exception.Error);
    }

    [Fact]
    public async Task Shared_deadline_is_checked_between_pdf_pages()
    {
        var time = new ManualTimeProvider();
        var context = Context(timeProvider: time, observer: stage => { if (stage == "pdf-page:2") time.Advance(TimeSpan.FromMinutes(1)); });
        await using var source = File.OpenRead(FixturePath("text-pages.pdf"));
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new PdfTextParser().ParseAsync(source, "application/pdf", context));
        Assert.Equal(DocumentParsingError.Timeout, exception.Error);
    }

    [Fact]
    public async Task Decoded_text_is_accounted_in_addition_to_source_bytes()
    {
        var bytes = Encoding.ASCII.GetBytes(new string('a', 50));
        var limits = new DocumentParsingLimits(100, 20, 110, TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new MarkdownTextParser().ParseAsync(
            new MemoryStream(bytes), "text/plain", Context(limits)));
        Assert.Equal(DocumentParsingError.MemoryLimitExceeded, exception.Error);
    }

    [Fact]
    public async Task Per_page_and_expanded_entry_limits_are_enforced()
    {
        await using var pdf = File.OpenRead(FixturePath("text-pages.pdf"));
        var pdfContext = Context(Limits() with { MaximumPageCharacters = 4 });
        var page = await Assert.ThrowsAsync<DocumentParsingException>(() => new PdfTextParser().ParseAsync(pdf, "application/pdf", pdfContext));
        Assert.Equal(DocumentParsingError.ResultLimitExceeded, page.Error);

        await using var docx = File.OpenRead(FixturePath("headings-table.docx"));
        var docxContext = Context(Limits() with { MaximumExpandedEntryBytes = 100 });
        var entry = await Assert.ThrowsAsync<DocumentParsingException>(() => new DocxParser().ParseAsync(docx,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", docxContext));
        Assert.Equal(DocumentParsingError.ResultLimitExceeded, entry.Error);
    }

    [Fact]
    public async Task Shared_deadline_is_checked_between_docx_elements()
    {
        var time = new ManualTimeProvider();
        var seen = 0;
        var context = Context(timeProvider: time, observer: stage => { if (stage == "docx-element" && ++seen == 2) time.Advance(TimeSpan.FromMinutes(1)); });
        await using var source = File.OpenRead(FixturePath("headings-table.docx"));
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new DocxParser().ParseAsync(source,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", context));
        Assert.Equal(DocumentParsingError.Timeout, exception.Error);
    }

    [Fact]
    public async Task Http_source_uses_shared_token_and_deadline_covers_download()
    {
        var time = new ManualTimeProvider();
        var handler = new AdvancingHandler(() => time.Advance(TimeSpan.FromSeconds(2)));
        var context = Context(Limits() with { ExecutionTimeout = TimeSpan.FromSeconds(1) }, time);
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new HttpDocumentSourceReader(new HttpClient(handler))
            .OpenReadAsync(new Uri("https://example.test/source"), context));
        Assert.True(handler.ReceivedCancelableToken);
        Assert.Equal(DocumentParsingError.Timeout, exception.Error);
    }

    private static DocumentParsingLimits Limits() => new(1024 * 1024, 20, 2 * 1024 * 1024, TimeSpan.FromSeconds(5));
    private static DocumentProcessingContext Context(DocumentParsingLimits? limits = null, TimeProvider? timeProvider = null, Action<string>? observer = null) =>
        new(limits ?? Limits(), TestContext.Current.CancellationToken, timeProvider ?? TimeProvider.System, observer);
    private static string FixturePath(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "documents", name));
    private static string BlockSignature(ParsedBlock block) => $"{block.Text}|{block.PageNumber}|{string.Join('/', block.Headings)}|{block.IsTable}|{block.TableRows}|{block.TableColumns}";

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan value) => _utcNow += value;
    }

    private sealed class AdvancingHandler(Action advance) : HttpMessageHandler
    {
        public bool ReceivedCancelableToken { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ReceivedCancelableToken = cancellationToken.CanBeCanceled;
            advance();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent("content"u8.ToArray()) });
        }
    }
}
