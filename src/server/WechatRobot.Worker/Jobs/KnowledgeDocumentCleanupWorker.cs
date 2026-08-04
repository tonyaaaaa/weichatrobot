using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Worker.Jobs;

public sealed class KnowledgeDocumentCleanupWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    private readonly string _owner = $"knowledge-delete-{Environment.MachineName}-{Guid.NewGuid():N}";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);
    private DateTime _nextRecoveryAtUtc = DateTime.MinValue;

    public async Task<bool> ProcessOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var job = await jobs.LeaseNextJobAsync(
            "CleanupKnowledgeDocument",
            _owner,
            now,
            LeaseDuration,
            token);
        if (job is null && now >= _nextRecoveryAtUtc)
        {
            _nextRecoveryAtUtc = now.Add(RecoveryInterval);
            var database = scope.ServiceProvider
                .GetRequiredService<WechatRobotDbContext>();
            if (await RecoverCompletedLegacyCleanupAsync(database, now, token))
                job = await jobs.LeaseNextJobAsync(
                    "CleanupKnowledgeDocument",
                    _owner,
                    now,
                    LeaseDuration,
                    token);
        }
        if (job is null) return false;
        try
        {
            using var payload = JsonDocument.Parse(job.PayloadJson);
            var documentId = payload.RootElement.GetProperty("documentId").GetGuid();
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var objectKeys = await database.KnowledgeDocumentVersions.AsNoTracking().Where(version => version.KnowledgeDocumentId == documentId && version.ObjectKey != "")
                .Select(version => version.ObjectKey).Distinct().ToArrayAsync(token);
            var knowledge = scope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>();
            while (await knowledge.GetDocumentIndexDrainDeadlineAsync(documentId, timeProvider.GetUtcNow().UtcDateTime, token) is { } drainDeadline)
            {
                var delay = drainDeadline - timeProvider.GetUtcNow().UtcDateTime + TimeSpan.FromMilliseconds(25);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            }
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
            foreach (var key in objectKeys) await storage.DeleteAsync(key, token);
            var vectors = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var contracts = await knowledge.GetDocumentVectorContractsAsync(documentId, token);
            foreach (var contract in contracts)
                if (contract.IsCollectionExclusive) await vectors.DeleteCollectionAsync(contract.Collection, token);
                else await vectors.DeleteVersionAsync(contract.Collection, contract.VersionId, token);
            contracts = (await knowledge.GetDocumentVectorContractsAsync(documentId, token)).Distinct().ToArray();
            foreach (var contract in contracts)
                if (contract.IsCollectionExclusive) await vectors.DeleteCollectionAsync(contract.Collection, token);
                else await vectors.DeleteVersionAsync(contract.Collection, contract.VersionId, token);
            foreach (var contract in contracts)
                if (contract.IsCollectionExclusive ? await vectors.InspectCollectionAsync(contract.Collection.Name, token) is not null :
                    (await vectors.InspectVersionAsync(contract.Collection, contract.VersionId, token)).Count != 0)
                    throw new InvalidOperationException($"Vector cleanup verification failed for {contract.Collection.Name}/{contract.VersionId:D}.");
            await DeleteDatabaseRecordsAsync(database, documentId, token);
            await jobs.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await jobs.FailJobAsync(job, $"Knowledge document physical cleanup failed: {exception.Message}", timeProvider.GetUtcNow().UtcDateTime, CancellationToken.None);
        }
        return true;
    }

    private static async Task<bool> RecoverCompletedLegacyCleanupAsync(
        WechatRobotDbContext database,
        DateTime nowUtc,
        CancellationToken token)
    {
        var strandedDocumentIds = await database.KnowledgeDocuments
            .AsNoTracking()
            .Where(document => document.IsDeleteRequested)
            .OrderBy(document => document.UpdatedAtUtc)
            .ThenBy(document => document.Id)
            .Select(document => document.Id)
            .Take(100)
            .ToArrayAsync(token);
        if (strandedDocumentIds.Length == 0)
            return false;

        var cleanupJobIds = strandedDocumentIds
            .Select(KnowledgeDocumentCleanupJobIdentity.Create)
            .ToArray();

        var completedJobs = new List<DurableJobEntity>();
        foreach (var batch in GuidBatchQuery.CreateBatches(cleanupJobIds))
        {
            var predicate = GuidBatchQuery.BuildPredicate<DurableJobEntity>(
                batch,
                job => job.Id);
            completedJobs.AddRange(await database.DurableJobs
                .Where(job =>
                    job.JobType == "CleanupKnowledgeDocument" &&
                    job.Status == "completed")
                .Where(predicate)
                .ToArrayAsync(token));
        }

        foreach (var completedJob in completedJobs)
            ResetForRecovery(completedJob, nowUtc);
        if (completedJobs.Count != 0)
            await database.SaveChangesAsync(token);
        return completedJobs.Count != 0;
    }

    private static void ResetForRecovery(
        DurableJobEntity job,
        DateTime nowUtc)
    {
        job.Status = "pending";
        job.CompletedAtUtc = null;
        job.NextAttemptAtUtc = nowUtc;
        job.LeaseOwner = null;
        job.LeaseExpiresAtUtc = null;
        job.UpdatedAtUtc = nowUtc;
        job.Version++;
    }

    private static async Task DeleteDatabaseRecordsAsync(
        WechatRobotDbContext database,
        Guid documentId,
        CancellationToken token)
    {
        var versions = await database.KnowledgeDocumentVersions
            .Where(version => version.KnowledgeDocumentId == documentId)
            .ToArrayAsync(token);
        var versionIds = versions.Select(version => version.Id).ToArray();
        if (versionIds.Length != 0)
        {
            var candidates = new List<KnowledgeCandidateEntity>();
            foreach (var batch in GuidBatchQuery.CreateBatches(versionIds))
            {
                var predicate = GuidBatchQuery.BuildPredicate<KnowledgeCandidateEntity>(
                    batch,
                    candidate => candidate.KnowledgeDocumentVersionId!.Value);
                candidates.AddRange(await database.KnowledgeCandidates
                    .Where(candidate => candidate.KnowledgeDocumentVersionId.HasValue)
                    .Where(predicate)
                    .ToArrayAsync(token));
            }
            foreach (var candidate in candidates)
                candidate.KnowledgeDocumentVersionId = null;
        }

        database.KnowledgeDocumentVersions.RemoveRange(versions);
        var document = await database.KnowledgeDocuments
            .SingleOrDefaultAsync(item => item.Id == documentId, token);
        if (document is not null)
            database.KnowledgeDocuments.Remove(document);
        await database.SaveChangesAsync(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessOnceAsync(stoppingToken)) await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}
