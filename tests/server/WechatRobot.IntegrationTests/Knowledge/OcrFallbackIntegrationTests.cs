using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Knowledge.Ocr;
using WechatRobot.Infrastructure.Knowledge.Parsing;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class OcrFallbackIntegrationTests
{
    [Fact]
    public async Task Empty_text_PDF_uses_OCR_but_partial_OCR_never_creates_previews()
    {
        var pdf = await File.ReadAllBytesAsync(Fixture("scanned-empty.pdf"), TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var versionId = await SeedVersion(db);
        var renderer = new SinglePageRenderer();
        var ocr = new ScannedPdfOcrService(db, renderer, new ResultClient(OcrPageStatus.Failed), new OcrProcessingOptions { MinimumExtractedTextCharacters = 20 });
        var service = CreatePreviewService(db, pdf, ocr);

        await Assert.ThrowsAsync<OcrIncompleteException>(() => service.GenerateAsync(versionId, new ChunkPolicy(ChunkPolicyKind.Smart), 0, TestContext.Current.CancellationToken));
        Assert.Empty(await db.KnowledgeChunkPreviews.ToArrayAsync(TestContext.Current.CancellationToken));

        ocr = new ScannedPdfOcrService(db, renderer, new ResultClient(OcrPageStatus.Completed), new OcrProcessingOptions { MinimumExtractedTextCharacters = 20 });
        service = CreatePreviewService(db, pdf, ocr);
        var result = await service.GenerateAsync(versionId, new ChunkPolicy(ChunkPolicyKind.Smart), 0, TestContext.Current.CancellationToken);
        Assert.Equal("扫描正文", Assert.Single(result.Items).Text);
        Assert.True(renderer.Calls >= 2);
    }

    [Fact]
    public async Task Text_PDF_above_threshold_does_not_invoke_OCR()
    {
        var pdf = await File.ReadAllBytesAsync(Fixture("text-pages.pdf"), TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var versionId = await SeedVersion(db);
        var renderer = new SinglePageRenderer();
        var ocr = new ScannedPdfOcrService(db, renderer, new ResultClient(OcrPageStatus.Completed), new OcrProcessingOptions { MinimumExtractedTextCharacters = 2 });
        await CreatePreviewService(db, pdf, ocr).GenerateAsync(versionId, new ChunkPolicy(ChunkPolicyKind.Smart), 0, TestContext.Current.CancellationToken);
        Assert.Equal(0, renderer.Calls);
    }

    private static KnowledgePreviewService CreatePreviewService(WechatRobotDbContext db, byte[] pdf, ScannedPdfOcrService ocr) => new(
        db, new MemoryReader(pdf), new DocumentParserSelector([new PdfTextParser()]), new ChunkingService(), new ChunkPreviewRepository(db),
        new DocumentParsingOptions { MaximumSourceBytes = 1024 * 1024, MaximumMemoryBytes = 4 * 1024 * 1024, MaximumPages = 10, ExecutionTimeoutSeconds = 5 },
        TimeProvider.System, ocr);

    private static async Task<Guid> SeedVersion(WechatRobotDbContext db)
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        db.KnowledgeDocuments.Add(new KnowledgeDocumentEntity { Id = documentId, Title = "scan", Status = "uploaded" });
        db.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity { Id = versionId, KnowledgeDocumentId = documentId, Version = 1,
            OriginalFileName = "scan.pdf", SafeFileName = "source.pdf", ContentType = "application/pdf", Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            ObjectKey = "key", PublicUrl = "https://example.test/key", Status = "uploaded" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return versionId;
    }

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", "fixtures", "documents", name));

    private sealed class MemoryReader(byte[] bytes) : IDocumentSourceReader
    {
        public Task<Stream> OpenReadAsync(Uri publicUrl, DocumentProcessingContext context)
        { context.ReserveSource(bytes.Length); return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)); }
    }
    private sealed class SinglePageRenderer : IPdfPageRenderer
    {
        public int Calls { get; private set; }
        public Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context) { Calls++; return Task.FromResult(1); }
        public Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(Stream pdf, IReadOnlyList<int> pages, DocumentProcessingContext context)
        { Calls++; return Task.FromResult<IReadOnlyList<OcrRenderedPage>>([new OcrRenderedPage(1, [1], 1, 1)]); }
    }
    private sealed class ResultClient(OcrPageStatus status) : IOcrClient
    {
        public Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(IReadOnlyList<OcrRenderedPage> pages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OcrPageResult>>([new OcrPageResult(1, status,
                status == OcrPageStatus.Completed ? [new OcrTextBlock(0, "扫描正文", 0.9)] : [], status == OcrPageStatus.Completed ? null : "failed")]);
    }
}
