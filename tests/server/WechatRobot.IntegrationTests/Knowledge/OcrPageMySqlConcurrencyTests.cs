using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge.Ocr;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class OcrPageMySqlConcurrencyTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Independent_workers_create_one_page_state_and_only_one_calls_OCR()
    {
        var dbOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(fixture.ConnectionString).Options;
        var versionId = Guid.NewGuid();
        await using (var setup = new WechatRobotDbContext(dbOptions))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var documentId = Guid.NewGuid();
            setup.KnowledgeDocuments.Add(new KnowledgeDocumentEntity { Id = documentId, Title = "ocr race", Status = "uploaded" });
            setup.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity { Id = versionId, KnowledgeDocumentId = documentId, Version = 1,
                OriginalFileName = "race.pdf", SafeFileName = "source.pdf", ContentType = "application/pdf", Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
                ObjectKey = "race", Status = "uploaded" });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var barrier = new Barrier(2);
        var client = new CountingClient();

        async Task<Exception?> RunAsync()
        {
            await using var db = new WechatRobotDbContext(dbOptions);
            var service = new ScannedPdfOcrService(db, new BarrierRenderer(barrier), client,
                new OcrProcessingOptions { MaximumPages = 1, MaximumImagePixels = 10, MaximumRenderedBytes = 10 });
            using var context = new DocumentProcessingContext(new DocumentParsingLimits(1024, 2, 4096, TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);
            try { await service.RecognizeAsync(versionId, new MemoryStream([1]), context); return null; }
            catch (OcrIncompleteException exception) { return exception; }
        }

        var outcomes = await Task.WhenAll(Task.Run(RunAsync, TestContext.Current.CancellationToken), Task.Run(RunAsync, TestContext.Current.CancellationToken));
        Assert.Contains(outcomes, item => item is null);
        Assert.Equal(1, client.Calls);
        await using var verify = new WechatRobotDbContext(dbOptions);
        var page = Assert.Single(await verify.KnowledgeOcrPages.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("completed", page.Status);
        Assert.Equal(1, page.AttemptCount);
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
        { Interlocked.Increment(ref _calls); return Task.FromResult<IReadOnlyList<OcrPageResult>>([new OcrPageResult(1, OcrPageStatus.Completed, [new OcrTextBlock(0, "text", 1)], null)]); }
    }
}
