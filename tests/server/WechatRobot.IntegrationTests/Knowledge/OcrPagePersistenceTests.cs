using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge.Ocr;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class OcrPagePersistenceTests
{
    [Fact]
    public async Task Expired_processing_page_is_claimed_by_only_one_worker()
    {
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(databaseName).Options;
        var versionId = Guid.NewGuid();
        await using (var setup = new WechatRobotDbContext(options))
        {
            setup.KnowledgeOcrPages.Add(new KnowledgeOcrPageEntity
            {
                KnowledgeDocumentVersionId = versionId,
                PageNumber = 1,
                Status = "processing",
                LeaseOwner = "crashed-worker",
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
                AttemptCount = 1
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var barrier = new Barrier(2);
        var client = new CountingClient();
        async Task RunAsync()
        {
            await using var database = new WechatRobotDbContext(options);
            var service = new ScannedPdfOcrService(database, new BarrierRenderer(barrier), client,
                new OcrProcessingOptions { MaximumPages = 1, MaximumImagePixels = 10, MaximumRenderedBytes = 10 });
            using var context = CreateContext();
            try { await service.RecognizeAsync(versionId, new MemoryStream([1]), context); }
            catch (OcrIncompleteException) { }
        }

        await Task.WhenAll(Task.Run(RunAsync, TestContext.Current.CancellationToken), Task.Run(RunAsync, TestContext.Current.CancellationToken));

        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Failed_pages_retry_without_rendering_or_recognizing_completed_pages()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var versionId = Guid.NewGuid();
        await using var db = new WechatRobotDbContext(options);
        db.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity
        {
            Id = versionId, KnowledgeDocumentId = Guid.NewGuid(), Version = 1, OriginalFileName = "scan.pdf", SafeFileName = "source.pdf",
            ContentType = "application/pdf", Sha256 = new string('d', 64), ObjectKey = "key", Status = "uploaded"
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var renderer = new FakeRenderer(2);
        var firstClient = new FakeClient(pages => pages.Select(page => page.PageNumber == 1
            ? new OcrPageResult(1, OcrPageStatus.Completed, [new OcrTextBlock(0, "第一页", 0.9)], null)
            : new OcrPageResult(2, OcrPageStatus.Failed, [], "OCR page failed.")).ToArray());
        var service = CreateService(db, renderer, firstClient);
        using var context = CreateContext();

        await Assert.ThrowsAsync<OcrIncompleteException>(() => service.RecognizeAsync(versionId, new MemoryStream([1]), context));
        Assert.Equal([1, 2], renderer.LastRequestedPages);
        var states = await db.KnowledgeOcrPages.OrderBy(item => item.PageNumber).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["completed", "failed"], states.Select(item => item.Status));

        var retryClient = new FakeClient(pages => [new OcrPageResult(2, OcrPageStatus.Completed, [new OcrTextBlock(0, "第二页", 0.8)], null)]);
        service = CreateService(db, renderer, retryClient);
        using var retryContext = CreateContext();
        var parsed = await service.RecognizeAsync(versionId, new MemoryStream([1]), retryContext);

        Assert.Equal([2], renderer.LastRequestedPages);
        Assert.Equal([2], retryClient.LastPages);
        Assert.Equal(["第一页", "第二页"], parsed.Blocks.Select(item => item.Text));
    }

    [Fact]
    public async Task Rejects_renderer_outputs_that_exceed_page_pixel_and_byte_limits()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var service = new ScannedPdfOcrService(db, new FakeRenderer(1, new OcrRenderedPage(1, new byte[11], 2, 2)), new FakeClient(_ => []),
            new OcrProcessingOptions { MaximumPages = 1, MaximumImagePixels = 3, MaximumRenderedBytes = 10 });
        using var context = CreateContext();
        await Assert.ThrowsAsync<DocumentParsingException>(() => service.RecognizeAsync(Guid.NewGuid(), new MemoryStream([1]), context));
    }

    [Fact]
    public async Task Render_failure_releases_page_lease_for_immediate_retry()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var versionId = Guid.NewGuid();
        var renderer = new FailingRenderer();
        var service = new ScannedPdfOcrService(db, renderer, new FakeClient(_ => []), new OcrProcessingOptions());
        using var context = CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(versionId, new MemoryStream([1]), context));

        var state = await db.KnowledgeOcrPages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("failed", state.Status);
        Assert.Null(state.LeaseOwner);
        Assert.Null(state.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Render_deadline_is_checked_after_native_renderer_returns()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var clock = new AdvancingTimeProvider();
        var service = new ScannedPdfOcrService(db, new AdvancingRenderer(clock), new FakeClient(_ => []),
            new OcrProcessingOptions { RenderTimeoutSeconds = 15 }, clock);
        using var context = new DocumentProcessingContext(new DocumentParsingLimits(1024, 10, 4096, TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken, clock);
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => service.RecognizeAsync(Guid.NewGuid(), new MemoryStream([1]), context));
        Assert.Equal(DocumentParsingError.Timeout, exception.Error);
    }

    [Fact]
    public async Task Shared_memory_budget_accounts_for_base64_HTTP_request_expansion()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var service = new ScannedPdfOcrService(db, new FakeRenderer(1), new FakeClient(_ => []), new OcrProcessingOptions());
        using var context = new DocumentProcessingContext(new DocumentParsingLimits(1024, 2, 4, TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => service.RecognizeAsync(Guid.NewGuid(), new MemoryStream([1]), context));
        Assert.Equal(DocumentParsingError.MemoryLimitExceeded, exception.Error);
    }

    private static ScannedPdfOcrService CreateService(WechatRobotDbContext db, FakeRenderer renderer, IOcrClient client) =>
        new(db, renderer, client, new OcrProcessingOptions { MaximumPages = 10, MaximumImagePixels = 100, MaximumRenderedBytes = 1024 });

    private static DocumentProcessingContext CreateContext() => new(
        new DocumentParsingLimits(1024, 10, 4096, TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);

    private sealed class FakeRenderer(int pageCount, OcrRenderedPage? fixedPage = null) : IPdfPageRenderer
    {
        public int[] LastRequestedPages { get; private set; } = [];
        public Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context) => Task.FromResult(pageCount);
        public Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(Stream pdf, IReadOnlyList<int> pageNumbers, DocumentProcessingContext context)
        {
            LastRequestedPages = pageNumbers.ToArray();
            return Task.FromResult<IReadOnlyList<OcrRenderedPage>>(pageNumbers.Select(number => fixedPage ?? new OcrRenderedPage(number, [1], 1, 1)).ToArray());
        }
    }

    private sealed class FakeClient(Func<IReadOnlyList<OcrRenderedPage>, IReadOnlyList<OcrPageResult>> result) : IOcrClient
    {
        public int[] LastPages { get; private set; } = [];
        public Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(IReadOnlyList<OcrRenderedPage> pages, CancellationToken cancellationToken)
        { LastPages = pages.Select(item => item.PageNumber).ToArray(); return Task.FromResult(result(pages)); }
    }

    private sealed class FailingRenderer : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context) => Task.FromResult(1);
        public Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(Stream pdf, IReadOnlyList<int> pageNumbers, DocumentProcessingContext context) =>
            throw new InvalidOperationException("renderer failed");
    }

    private sealed class AdvancingRenderer(AdvancingTimeProvider clock) : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context)
        { clock.Advance(TimeSpan.FromSeconds(16)); return Task.FromResult(1); }
        public Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(Stream pdf, IReadOnlyList<int> pageNumbers, DocumentProcessingContext context) =>
            Task.FromResult<IReadOnlyList<OcrRenderedPage>>([]);
    }
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class BarrierRenderer(Barrier barrier) : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context)
        { barrier.SignalAndWait(context.Token); return Task.FromResult(1); }
        public Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(Stream pdf, IReadOnlyList<int> pageNumbers, DocumentProcessingContext context) =>
            Task.FromResult<IReadOnlyList<OcrRenderedPage>>([new OcrRenderedPage(1, [1], 1, 1)]);
    }

    private sealed class CountingClient : IOcrClient
    {
        private int _calls;
        public int Calls => _calls;
        public Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(IReadOnlyList<OcrRenderedPage> pages, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<OcrPageResult>>([new OcrPageResult(1, OcrPageStatus.Completed, [new OcrTextBlock(0, "text", 1)], null)]);
        }
    }
}
