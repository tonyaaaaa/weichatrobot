using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class ChunkPreviewMySqlConcurrencyTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;
    public ChunkPreviewMySqlConcurrencyTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Simultaneous_approval_has_one_complete_active_set_and_idempotent_or_concurrency_loser()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).Options;
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await using (var setup = new WechatRobotDbContext(options))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            setup.KnowledgeDocuments.Add(new KnowledgeDocumentEntity { Id = documentId, Title = "race", Status = "uploaded" });
            setup.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity { Id = versionId, KnowledgeDocumentId = documentId, Version = 1,
                OriginalFileName = "race.txt", SafeFileName = "source.txt", ContentType = "text/plain", Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
                ObjectKey = "race", PublicUrl = "https://example.test/race", Status = "uploaded" });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            await new ChunkPreviewRepository(setup).ReplaceAsync(versionId,
                [new ChunkPreview(Guid.NewGuid(), 0, "first", 1, [], false, null, null), new ChunkPreview(Guid.NewGuid(), 1, "second", 2, [], false, null, null)],
                0, TestContext.Current.CancellationToken);
        }

        using var barrier = new Barrier(2);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var observations = new System.Collections.Concurrent.ConcurrentBag<int>();
        var monitorReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = Task.Run(async () =>
        {
            await using var db = new WechatRobotDbContext(options);
            try
            {
                while (true)
                {
                    observations.Add(await db.KnowledgeChunks.AsNoTracking().CountAsync(item => item.KnowledgeDocumentVersionId == versionId, monitorCancellation.Token));
                    monitorReady.TrySetResult();
                    await Task.Delay(1, monitorCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested) { }
        }, TestContext.Current.CancellationToken);
        await monitorReady.Task.WaitAsync(TestContext.Current.CancellationToken);
        async Task<Exception?> ApproveAsync()
        {
            await using var db = new WechatRobotDbContext(options);
            barrier.SignalAndWait(TestContext.Current.CancellationToken);
            try { _ = await new ChunkPreviewRepository(db).ApproveAsync(versionId, 1, TestContext.Current.CancellationToken); return null; }
            catch (Exception exception) when (exception is ChunkPreviewConcurrencyException or DbUpdateConcurrencyException) { return exception; }
        }

        Exception?[] outcomes;
        try { outcomes = await Task.WhenAll(Task.Run(ApproveAsync, TestContext.Current.CancellationToken), Task.Run(ApproveAsync, TestContext.Current.CancellationToken)); }
        finally { monitorCancellation.Cancel(); await monitor; }
        Assert.Contains(outcomes, outcome => outcome is null);
        Assert.All(outcomes, outcome => Assert.True(outcome is null or ChunkPreviewConcurrencyException or DbUpdateConcurrencyException));

        await using var verify = new WechatRobotDbContext(options);
        var version = await verify.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(item => item.Id == versionId, TestContext.Current.CancellationToken);
        var active = await verify.KnowledgeChunks.AsNoTracking().Where(item => item.KnowledgeDocumentVersionId == versionId).OrderBy(item => item.Sequence).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal("approved", version.Status);
        Assert.Equal(1, version.PreviewRevision);
        Assert.Equal(["first", "second"], active.Select(item => item.Text));
        Assert.Equal(2, active.Select(item => item.Id).Distinct().Count());
        Assert.NotEmpty(observations);
        Assert.All(observations, count => Assert.Contains(count, new[] { 0, 2 }));
    }
}
