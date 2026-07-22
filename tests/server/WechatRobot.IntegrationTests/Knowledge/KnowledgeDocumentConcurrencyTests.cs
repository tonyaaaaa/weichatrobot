using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentConcurrencyTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;
    public KnowledgeDocumentConcurrencyTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Stale_failure_from_independent_context_cannot_regress_committed_success()
    {
        var seeded = await SeedUploadingAsync();
        var pause = new PauseBeforeSaveInterceptor();
        await using var staleContext = CreateContext(pause);
        await using var successContext = CreateContext();
        var staleStore = new KnowledgeDocumentStore(staleContext);
        var successStore = new KnowledgeDocumentStore(successContext);

        pause.Arm();
        var staleFailure = staleStore.MarkFailedAsync(seeded.Pending, TestContext.Current.CancellationToken);
        await pause.WaitUntilSavingAsync();
        Assert.True(await successStore.MarkUploadedAsync(seeded.Pending, Stored(seeded.Pending), TestContext.Current.CancellationToken));
        pause.Release();
        await staleFailure;

        await using var verify = CreateContext();
        Assert.Equal("uploaded", (await verify.KnowledgeDocumentVersions.SingleAsync(item => item.Id == seeded.VersionId, TestContext.Current.CancellationToken)).Status);
        var uploadJob = await verify.DurableJobs.SingleAsync(item => item.Id == seeded.UploadJobId, TestContext.Current.CancellationToken);
        Assert.Equal("completed", uploadJob.Status);
        Assert.True(uploadJob.Version > seeded.UploadJobVersion);
        Assert.Equal("pending", (await verify.DurableJobs.SingleAsync(item => item.Id == seeded.ParseJobId, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Stale_success_from_independent_context_cannot_reactivate_after_delete()
    {
        var seeded = await SeedUploadingAsync();
        var pause = new PauseBeforeSaveInterceptor();
        await using var staleContext = CreateContext(pause);
        await using var deleteContext = CreateContext();
        var staleStore = new KnowledgeDocumentStore(staleContext);
        var deleteStore = new KnowledgeDocumentStore(deleteContext);

        pause.Arm();
        var staleSuccess = staleStore.MarkUploadedAsync(seeded.Pending, Stored(seeded.Pending), TestContext.Current.CancellationToken);
        await pause.WaitUntilSavingAsync();
        Assert.True(await deleteStore.RequestPhysicalDeleteAsync(seeded.DocumentId, TestContext.Current.CancellationToken));
        pause.Release();
        try { _ = await staleSuccess; } catch (DbUpdateConcurrencyException) { }

        await using var verify = CreateContext();
        Assert.Equal("disabled", (await verify.KnowledgeDocuments.SingleAsync(item => item.Id == seeded.DocumentId, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("disabled", (await verify.KnowledgeDocumentVersions.SingleAsync(item => item.Id == seeded.VersionId, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("cancelled", (await verify.DurableJobs.SingleAsync(item => item.Id == seeded.ParseJobId, TestContext.Current.CancellationToken)).Status);
        Assert.Single(await verify.DurableJobs.Where(item => item.JobType == "CleanupKnowledgeDocument" && item.PayloadJson.Contains(seeded.DocumentId.ToString())).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    private async Task<SeededUpload> SeedUploadingAsync()
    {
        await using var database = CreateContext();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var document = new KnowledgeDocumentEntity { Title = $"concurrency-{Guid.NewGuid():N}" };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id, Version = 1, OriginalFileName = "source.txt", SafeFileName = "source.txt",
            ContentType = "text/plain", Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'), SizeBytes = 7,
            ObjectKey = $"wechatrobot/knowledge/{document.Id:N}/1/source/source.txt", StagedContent = "content"u8.ToArray()
        };
        var uploadJob = new DurableJobEntity
        {
            JobType = "UploadKnowledgeDocument", Status = "leased", LeaseOwner = "stale-worker",
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1), PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { documentId = document.Id, versionId = version.Id })
        };
        var parseJob = new DurableJobEntity
        {
            JobType = "ParseKnowledgeDocument", Status = "blocked",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { documentId = document.Id, versionId = version.Id })
        };
        database.AddRange(document, version, uploadJob, parseJob);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new SeededUpload(document.Id, version.Id, uploadJob.Id, uploadJob.Version, parseJob.Id,
            new PendingDocumentUpload(document.Id, version.Id, 1, version.ObjectKey, version.SafeFileName, version.ContentType, version.Sha256, version.StagedContent));
    }

    private WechatRobotDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).AddInterceptors(interceptors).Options;
        return new WechatRobotDbContext(options);
    }

    private static StoredObject Stored(PendingDocumentUpload upload) => new(upload.ObjectKey, new Uri($"https://public.example.test/{upload.ObjectKey}"));
    private sealed record SeededUpload(Guid DocumentId, Guid VersionId, Guid UploadJobId, int UploadJobVersion, Guid ParseJobId, PendingDocumentUpload Pending);
}

public sealed class PauseBeforeSaveInterceptor : DbCommandInterceptor
{
    private readonly TaskCompletionSource _saving = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _armed;
    public void Arm() => _armed = true;
    public Task WaitUntilSavingAsync() => _saving.Task;
    public void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!_armed || !command.CommandText.Contains("UPDATE `durable_job`", StringComparison.OrdinalIgnoreCase)) return result;
        _armed = false;
        _saving.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        return result;
    }
}
