using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Memory;
using WechatRobot.Application.Models;
using WechatRobot.Domain.Memory;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Worker.Jobs;

public sealed class MemoryExtractionWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MemoryExtractionWorker> logger) : BackgroundService
{
    private readonly string leaseOwner = $"memory-{Environment.MachineName}-{Guid.NewGuid():N}";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var job = await repository.LeaseNextJobAsync(
            "ExtractConversationMemory",
            leaseOwner,
            now,
            LeaseDuration,
            cancellationToken);
        if (job is null)
        {
            return false;
        }

        try
        {
            await ProcessAsync(scope.ServiceProvider, job, cancellationToken);
            await repository.CompleteJobAsync(job.Id, job.LeaseOwner, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (MemoryExtractionException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await repository.FailJobAsync(job, exception.FailureCode, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Memory extraction job {JobId} failed with a sanitized failure.", job.Id);
            await repository.FailJobAsync(job, "memory_processing_failed", timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }

        return true;
    }

    private static async Task ProcessAsync(
        IServiceProvider services,
        LeasedDurableJob job,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<Payload>(
            job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new MemoryExtractionException("memory_content_invalid");

        var database = services.GetRequiredService<WechatRobotDbContext>();
        var group = await database.GroupProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == payload.GroupProfileId, cancellationToken);
        if (group is null || !group.IsEnabled || group.ArchivedAtUtc is not null)
        {
            return;
        }

        var source = await database.ConversationMessages.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == payload.MessageId, cancellationToken)
            ?? throw new MemoryExtractionException("memory_invalid_source");
        if (source.ConversationSessionId != payload.ConversationSessionId)
        {
            throw new MemoryExtractionException("memory_invalid_source");
        }

        var model = await database.ModelConfigs.AsNoTracking()
            .Where(x => x.ConfigurationType == "chat" && x.IsEnabled && x.IsDefault)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new MemoryExtractionException("memory_model_unavailable");
        if (payload.ModelConfigurationId != Guid.Empty &&
            (model.Id != payload.ModelConfigurationId ||
             payload.ModelConfigurationVersion != 0 && model.Version != payload.ModelConfigurationVersion))
        {
            throw new MemoryExtractionException("memory_model_unavailable");
        }

        var rows = await database.ConversationMessages.AsNoTracking()
            .Where(x => x.ConversationSessionId == payload.ConversationSessionId &&
                        x.SessionSequence <= source.SessionSequence)
            .OrderByDescending(x => x.SessionSequence)
            .Take(30)
            .OrderBy(x => x.SessionSequence)
            .Select(x => new MemoryExtractionMessage(x.Id, x.Role, x.Text, x.CreatedAtUtc))
            .ToArrayAsync(cancellationToken);

        var scope = MemoryScope.Create(
            MemoryScopeType.User,
            source.RobotConfigId,
            group.Id,
            source.SenderDisplayName,
            source.SenderDisplayName);
        var context = new MemoryExtractionContext(scope, rows);
        var configuration = services.GetRequiredService<ModelConfigurationService>()
            .ToProviderConfiguration(new ModelConfigurationRecord(
                model.Id,
                model.Name,
                model.Provider,
                model.BaseUrl,
                model.Model,
                model.EncryptedApiKey,
                model.TimeoutSeconds,
                model.MaxRetries,
                model.IsEnabled,
                model.IsDefault,
                model.EmbeddingDimension,
                model.WebSearchMode));
        var extraction = await services.GetRequiredService<MemoryExtractionService>()
            .ExtractAsync(configuration, context, cancellationToken);
        await services.GetRequiredService<MemoryOrganizationService>()
            .OrganizeAsync(
                context,
                extraction,
                model.Id,
                payload.ConversationSessionId,
                configuration,
                cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessOnceAsync(stoppingToken))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private sealed class Payload
    {
        public Guid MessageId { get; init; }
        public Guid ConversationSessionId { get; init; }
        public Guid ModelConfigurationId { get; init; }
        public int ModelConfigurationVersion { get; init; }
        public Guid GroupProfileId { get; init; }
    }
}
