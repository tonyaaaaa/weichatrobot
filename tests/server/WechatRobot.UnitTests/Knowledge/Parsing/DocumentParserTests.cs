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
        var utf8 = await parser.ParseAsync(utf8Source, "text/plain", Limits(), TestContext.Current.CancellationToken);
        await using var gbSource = File.OpenRead(FixturePath("gb18030.txt"));
        var gb = await parser.ParseAsync(gbSource, "text/plain", Limits(), TestContext.Current.CancellationToken);
        Assert.Equal("第一行\n第二行", Assert.Single(utf8.Blocks).Text);
        Assert.Equal("中文内容", Assert.Single(gb.Blocks).Text);
        await using var secondSource = File.OpenRead(FixturePath("utf8.txt"));
        var second = await parser.ParseAsync(secondSource, "text/plain", Limits(), TestContext.Current.CancellationToken);
        Assert.Equal(utf8.Blocks.Select(BlockSignature), second.Blocks.Select(BlockSignature));
    }

    [Fact]
    public async Task Markdown_parser_preserves_heading_hierarchy()
    {
        var parser = new MarkdownTextParser();
        await using var source = File.OpenRead(FixturePath("headings.md"));
        var result = await parser.ParseAsync(source, "text/markdown", Limits(), TestContext.Current.CancellationToken);
        Assert.Collection(result.Blocks,
            block => { Assert.Equal("介绍", block.Text); Assert.Equal(["产品"], block.Headings); },
            block => { Assert.Equal("步骤", block.Text); Assert.Equal(["产品", "安装"], block.Headings); });
    }

    [Fact]
    public async Task Docx_parser_preserves_headings_and_table_metadata()
    {
        await using var stream = File.OpenRead(FixturePath("headings-table.docx"));
        var result = await new DocxParser().ParseAsync(stream, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Limits(), TestContext.Current.CancellationToken);
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
            new MemoryStream([0x81]), "text/plain", Limits(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DocumentParsingException>(() => selector.Select("text/plain").ParseAsync(
            new MemoryStream(new byte[11]), "text/plain", Limits() with { MaximumSourceBytes = 10 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pdf_parser_preserves_pages_and_marks_empty_text_for_future_ocr()
    {
        await using var textPdf = File.OpenRead(FixturePath("text-pages.pdf"));
        var parsed = await new PdfTextParser().ParseAsync(textPdf, "application/pdf", Limits(), TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], parsed.Blocks.Select(block => block.PageNumber));
        Assert.Equal(["Page one", "Page two"], parsed.Blocks.Select(block => block.Text));
        await using var scanned = File.OpenRead(FixturePath("scanned-empty.pdf"));
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new PdfTextParser().ParseAsync(scanned, "application/pdf", Limits(), TestContext.Current.CancellationToken));
        Assert.Equal(DocumentParsingError.EmptyTextPdf, exception.Error);

        await using var tooManyPages = File.OpenRead(FixturePath("text-pages.pdf"));
        var pageLimit = await Assert.ThrowsAsync<DocumentParsingException>(() => new PdfTextParser().ParseAsync(tooManyPages, "application/pdf", Limits() with { MaximumPages = 1 }, TestContext.Current.CancellationToken));
        Assert.Equal(DocumentParsingError.PageLimitExceeded, pageLimit.Error);
    }

    [Fact]
    public async Task Parser_read_honors_execution_timeout()
    {
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new MarkdownTextParser().ParseAsync(
            new SlowStream(), "text/plain", Limits() with { ExecutionTimeout = TimeSpan.FromMilliseconds(20) }, TestContext.Current.CancellationToken));
        Assert.Equal(DocumentParsingError.Timeout, exception.Error);
    }

    [Fact]
    public async Task Docx_parser_rejects_expanded_content_over_memory_limit()
    {
        await using var source = File.OpenRead(FixturePath("headings-table.docx"));
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => new DocxParser().ParseAsync(source,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Limits() with { MaximumSourceBytes = 1024, MaximumMemoryBytes = 1100 }, TestContext.Current.CancellationToken));
        Assert.Equal(DocumentParsingError.MemoryLimitExceeded, exception.Error);
    }

    private static DocumentParsingLimits Limits() => new(1024 * 1024, 20, 2 * 1024 * 1024, TimeSpan.FromSeconds(5));
    private static string FixturePath(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "documents", name));
    private static string BlockSignature(ParsedBlock block) => $"{block.Text}|{block.PageNumber}|{string.Join('/', block.Headings)}|{block.IsTable}|{block.TableRows}|{block.TableColumns}";

    private sealed class SlowStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); return 0; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
