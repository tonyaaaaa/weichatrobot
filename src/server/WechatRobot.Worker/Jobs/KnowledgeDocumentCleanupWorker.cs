using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Worker.Jobs;

public sealed class KnowledgeDocumentCleanupWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : BackgroundService
{
    private readonly string _owner = $"knowledge-delete-{Environment.MachineName}-{Guid.NewGuid():N}";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<bool> ProcessOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var job = await jobs.LeaseNextJobAsync("CleanupKnowledgeDocument", _owner, timeProvider.GetUtcNow().UtcDateTime, LeaseDuration, token);
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
            await jobs.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await jobs.FailJobAsync(job, $"Knowledge document physical cleanup failed: {exception.Message}", timeProvider.GetUtcNow().UtcDateTime, CancellationToken.None);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessOnceAsync(stoppingToken)) await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}
