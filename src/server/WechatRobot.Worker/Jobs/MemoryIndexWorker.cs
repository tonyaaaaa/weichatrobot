using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Memory;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Worker.Jobs;

public sealed class MemoryIndexWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly string leaseOwner = $"memory-index-{Environment.MachineName}-{Guid.NewGuid():N}";

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var jobs = services.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var job = await jobs.LeaseNextJobAsync(
            "IndexMemoryEntry",
            leaseOwner,
            now,
            TimeSpan.FromMinutes(2),
            cancellationToken);
        if (job is null) return false;

        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
            var entryId = payload.RootElement.GetProperty("memoryEntryId").GetGuid();
            var database = services.GetRequiredService<WechatRobotDbContext>();
            var entry = await database.MemoryEntries.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == entryId, cancellationToken);
            if (entry is not null && entry.Status == "active")
            {
                var model = await database.ModelConfigs.AsNoTracking()
                    .SingleAsync(x => x.ConfigurationType == "embedding" && x.IsEnabled && x.IsDefault, cancellationToken);
                var configuration = services.GetRequiredService<ModelConfigurationService>()
                    .ToProviderConfiguration(new ModelConfigurationRecord(
                        model.Id, model.Name, model.Provider, model.BaseUrl, model.Model,
                        model.EncryptedApiKey, model.TimeoutSeconds, model.MaxRetries,
                        model.IsEnabled, model.IsDefault, model.EmbeddingDimension, model.WebSearchMode));
                var vector = (await services.GetRequiredService<IEmbeddingClient>()
                    .CreateEmbeddingsAsync(configuration, new EmbeddingBatchRequest([entry.Content]), cancellationToken))
                    .Vectors.Single();
                if (model.EmbeddingDimension is not > 0 || vector.Count != model.EmbeddingDimension)
                    throw new InvalidOperationException("memory_embedding_dimension_mismatch");
                await services.GetRequiredService<IMemoryVectorIndex>().IndexAsync(
                    new MemoryVectorDocument(
                        entry.Id, entry.ScopeType, entry.RobotConfigId, entry.GroupProfileId,
                        entry.SubjectKey, entry.MemoryType, entry.StatusVersion, 1, vector),
                    model.EmbeddingDimension.Value,
                    VectorDistance.Cosine,
                    cancellationToken);
            }
            await jobs.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await jobs.FailJobAsync(job, "memory_index_failed", timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        return true;
    }

    public async Task<bool> ProcessRemoveOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var jobs = services.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var job = await jobs.LeaseNextJobAsync(
            "RemoveMemoryEntryFromIndex",
            leaseOwner,
            now,
            TimeSpan.FromMinutes(2),
            cancellationToken);
        if (job is null) return false;
        try
        {
            using var payload = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
            var entryId = payload.RootElement.GetProperty("memoryEntryId").GetGuid();
            var database = services.GetRequiredService<WechatRobotDbContext>();
            var model = await database.ModelConfigs.AsNoTracking()
                .SingleAsync(x => x.ConfigurationType == "embedding" && x.IsEnabled && x.IsDefault, cancellationToken);
            if (model.EmbeddingDimension is not > 0)
                throw new InvalidOperationException("memory_embedding_dimension_missing");
            await services.GetRequiredService<IMemoryVectorIndex>().RemoveAsync(
                entryId,
                model.EmbeddingDimension.Value,
                VectorDistance.Cosine,
                1,
                cancellationToken);
            await jobs.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await jobs.FailJobAsync(job, "memory_index_remove_failed", timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await ProcessOnceAsync(stoppingToken);
            processed |= await ProcessRemoveOnceAsync(stoppingToken);
            if (!processed)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
