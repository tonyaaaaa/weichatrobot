using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Models;

public sealed class ModelConfigurationManager(
    WechatRobotDbContext database,
    ModelConfigurationService service,
    IChatCompletionClient chatClient,
    IEmbeddingClient embeddingClient,
    TimeProvider timeProvider)
{
    public Task<List<ModelConfigEntity>> ListAsync(CancellationToken cancellationToken) =>
        database.ModelConfigs.AsNoTracking().OrderBy(config => config.Name).ToListAsync(cancellationToken);

    public async Task<ModelConfigurationMutationResult> CreateAsync(
        CreateModelConfigurationCommand command,
        CancellationToken cancellationToken)
    {
        var errors = Validate(command.Name, command.ConfigurationType, command.BaseUrl, command.Model,
            command.TimeoutSeconds, command.MaxRetries);
        if (errors is not null)
        {
            return ModelConfigurationMutationResult.Invalid(errors);
        }

        var name = command.Name.Trim();
        var normalizedName = NormalizeName(name);
        if (await database.ModelConfigs.AnyAsync(item => item.NormalizedName == normalizedName, cancellationToken))
        {
            return ModelConfigurationMutationResult.NameConflict();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var hasApiKey = !string.IsNullOrWhiteSpace(command.ApiKey);
        var entity = new ModelConfigEntity
        {
            Name = name,
            NormalizedName = normalizedName,
            Provider = command.Provider.Trim(),
            ConfigurationType = command.ConfigurationType.Trim().ToLowerInvariant(),
            BaseUrl = command.BaseUrl.Trim().TrimEnd('/'),
            Model = command.Model.Trim(),
            EncryptedApiKey = service.ProtectSubmittedApiKey(command.ApiKey, null),
            TimeoutSeconds = command.TimeoutSeconds,
            MaxRetries = command.MaxRetries,
            IsEnabled = false,
            IsDefault = false,
            ConnectionStatus = ModelConnectionStatus.Untested,
            ApiKeyVersion = hasApiKey ? 1 : 0,
            Version = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        database.ModelConfigs.Add(entity);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ModelConfigurationMutationResult.NameConflict();
        }

        return ModelConfigurationMutationResult.Succeeded(entity);
    }

    public async Task<ModelConfigurationMutationResult> UpsertCompatibilityAsync(
        string routeName,
        CompatibilityModelConfigurationCommand command,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeName(routeName);
        var entity = await database.ModelConfigs.SingleOrDefaultAsync(
            item => item.NormalizedName == normalizedName || item.Name == routeName,
            cancellationToken);

        var result = entity is null
            ? await CreateAsync(
                new CreateModelConfigurationCommand(
                    routeName, command.Provider, command.ConfigurationType, command.BaseUrl, command.Model,
                    command.ApiKey, command.TimeoutSeconds, command.MaxRetries),
                cancellationToken)
            : await UpdateAsync(
                entity.Id,
                new UpdateModelConfigurationCommand(
                    entity.Name, command.Provider, command.ConfigurationType, command.BaseUrl, command.Model,
                    command.ApiKey, command.TimeoutSeconds, command.MaxRetries, entity.Version),
                cancellationToken);

        if (result.Status != ModelConfigurationMutationStatus.Success)
        {
            return result;
        }

        return result;
    }

    public async Task<ModelConfigurationMutationResult> TestConnectionAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken)
    {
        var entity = await database.ModelConfigs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
        {
            return ModelConfigurationMutationResult.NotFound();
        }

        try
        {
            var configuration = service.ToProviderConfiguration(ToRecord(entity));
            if (entity.ConfigurationType.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                await chatClient.CompleteAsync(
                    configuration,
                    new ChatCompletionRequest([new ChatMessage("user", "connection test")]),
                    cancellationToken);
            }
            else if (entity.ConfigurationType.Equals("embedding", StringComparison.OrdinalIgnoreCase))
            {
                await embeddingClient.CreateEmbeddingsAsync(
                    configuration,
                    new EmbeddingBatchRequest(["connection test"]),
                    cancellationToken);
            }
            else
            {
                return ModelConfigurationMutationResult.Invalid(
                    new Dictionary<string, string[]>
                    {
                        ["configurationType"] = ["Configuration type must be chat or embedding."]
                    });
            }

            entity.ConnectionStatus = ModelConnectionStatus.Succeeded;
            entity.LastTestedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            entity.LastTestFailureSummary = null;
            entity.TestedConfigurationFingerprint = CurrentFingerprint(entity);
            entity.Version++;
            entity.UpdatedAtUtc = entity.LastTestedAtUtc.Value;
            await database.SaveChangesAsync(cancellationToken);
            return ModelConfigurationMutationResult.Succeeded(entity);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            entity.ConnectionStatus = ModelConnectionStatus.Failed;
            entity.LastTestedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            entity.LastTestFailureSummary = ClassifyFailure(exception);
            entity.TestedConfigurationFingerprint = null;
            entity.Version++;
            entity.UpdatedAtUtc = entity.LastTestedAtUtc.Value;
            await database.SaveChangesAsync(cancellationToken);
            return ModelConfigurationMutationResult.ProviderFailure(entity);
        }
    }

    public async Task<ModelConfigurationMutationResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        int version,
        string actor,
        CancellationToken cancellationToken)
    {
        var entity = await database.ModelConfigs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
        {
            return ModelConfigurationMutationResult.NotFound();
        }

        if (entity.Version != version)
        {
            return ModelConfigurationMutationResult.ConcurrencyConflict();
        }

        if (!enabled && entity.IsDefault)
        {
            return ModelConfigurationMutationResult.DefaultDisableForbidden();
        }

        if (enabled && !HasCurrentSuccessfulTest(entity))
        {
            return ModelConfigurationMutationResult.TestRequired();
        }

        entity.IsEnabled = enabled;
        entity.Version++;
        entity.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(cancellationToken);
        return ModelConfigurationMutationResult.Succeeded(entity);
    }

    public async Task<ModelConfigurationMutationResult> SetDefaultAsync(
        Guid id,
        bool isDefault,
        int version,
        string actor,
        CancellationToken cancellationToken)
    {
        var entity = await database.ModelConfigs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
        {
            return ModelConfigurationMutationResult.NotFound();
        }

        if (entity.Version != version)
        {
            return ModelConfigurationMutationResult.ConcurrencyConflict();
        }

        if (isDefault && !HasCurrentSuccessfulTest(entity))
        {
            return ModelConfigurationMutationResult.TestRequired();
        }

        await using var transaction = database.Database.ProviderName?.Contains("InMemory", StringComparison.Ordinal) != true
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (isDefault)
        {
            var currentDefaults = await database.ModelConfigs
                .Where(item => item.Id != entity.Id &&
                               item.ConfigurationType == entity.ConfigurationType &&
                               item.IsDefault)
                .ToListAsync(cancellationToken);
            foreach (var current in currentDefaults)
            {
                current.IsDefault = false;
                current.Version++;
                current.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            }

            entity.IsEnabled = true;
            entity.IsDefault = true;
        }
        else
        {
            entity.IsDefault = false;
        }

        entity.Version++;
        entity.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return ModelConfigurationMutationResult.ConcurrencyConflict();
        }
        catch (DbUpdateException)
        {
            return ModelConfigurationMutationResult.DefaultConflict();
        }

        return ModelConfigurationMutationResult.Succeeded(entity);
    }

    public async Task<ModelConfigurationMutationResult> UpdateAsync(
        Guid id,
        UpdateModelConfigurationCommand command,
        CancellationToken cancellationToken)
    {
        var errors = Validate(command.Name, command.ConfigurationType, command.BaseUrl, command.Model,
            command.TimeoutSeconds, command.MaxRetries);
        if (errors is not null)
        {
            return ModelConfigurationMutationResult.Invalid(errors);
        }

        var entity = await database.ModelConfigs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
        {
            return ModelConfigurationMutationResult.NotFound();
        }

        if (entity.Version != command.Version)
        {
            return ModelConfigurationMutationResult.ConcurrencyConflict();
        }

        var name = command.Name.Trim();
        var normalizedName = NormalizeName(name);
        if (await database.ModelConfigs.AnyAsync(
                item => item.Id != id && item.NormalizedName == normalizedName,
                cancellationToken))
        {
            return ModelConfigurationMutationResult.NameConflict();
        }

        var provider = command.Provider.Trim();
        var configurationType = command.ConfigurationType.Trim().ToLowerInvariant();
        var baseUrl = command.BaseUrl.Trim().TrimEnd('/');
        var model = command.Model.Trim();
        var replacesApiKey = !string.IsNullOrWhiteSpace(command.ApiKey);
        var invalidatesConnection =
            !string.Equals(entity.Provider, provider, StringComparison.Ordinal) ||
            !string.Equals(entity.ConfigurationType, configurationType, StringComparison.Ordinal) ||
            !string.Equals(entity.BaseUrl, baseUrl, StringComparison.Ordinal) ||
            !string.Equals(entity.Model, model, StringComparison.Ordinal) ||
            replacesApiKey;

        entity.Name = name;
        entity.NormalizedName = normalizedName;
        entity.Provider = provider;
        entity.ConfigurationType = configurationType;
        entity.BaseUrl = baseUrl;
        entity.Model = model;
        entity.EncryptedApiKey = service.ProtectSubmittedApiKey(command.ApiKey, entity.EncryptedApiKey);
        entity.TimeoutSeconds = command.TimeoutSeconds;
        entity.MaxRetries = command.MaxRetries;
        entity.ApiKeyVersion += replacesApiKey ? 1 : 0;
        entity.Version++;
        entity.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        if (invalidatesConnection)
        {
            entity.ConnectionStatus = ModelConnectionStatus.Untested;
            entity.LastTestedAtUtc = null;
            entity.LastTestFailureSummary = null;
            entity.TestedConfigurationFingerprint = null;
            entity.IsEnabled = false;
            entity.IsDefault = false;
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ModelConfigurationMutationResult.ConcurrencyConflict();
        }
        catch (DbUpdateException)
        {
            return ModelConfigurationMutationResult.NameConflict();
        }

        return ModelConfigurationMutationResult.Succeeded(entity);
    }

    public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();

    private bool HasCurrentSuccessfulTest(ModelConfigEntity entity) =>
        entity.ConnectionStatus == ModelConnectionStatus.Succeeded &&
        string.Equals(
            entity.TestedConfigurationFingerprint,
            CurrentFingerprint(entity),
            StringComparison.Ordinal);

    private string CurrentFingerprint(ModelConfigEntity entity) =>
        service.ComputeFingerprint(ToRecord(entity), entity.ConfigurationType, entity.ApiKeyVersion);

    private static ModelConfigurationRecord ToRecord(ModelConfigEntity entity) =>
        new(entity.Id, entity.Name, entity.Provider, entity.BaseUrl, entity.Model, entity.EncryptedApiKey,
            entity.TimeoutSeconds, entity.MaxRetries, entity.IsEnabled, entity.IsDefault);

    private static string ClassifyFailure(Exception exception)
    {
        if (exception is OperationCanceledException ||
            exception is ModelUnavailableException { InnerException: OperationCanceledException })
        {
            return "timeout";
        }

        if (exception is HttpRequestException ||
            exception is ModelUnavailableException { InnerException: HttpRequestException })
        {
            return "http_error";
        }

        return "invalid_response";
    }

    private static Dictionary<string, string[]>? Validate(
        string? name,
        string? configurationType,
        string? baseUrl,
        string? model,
        int timeoutSeconds,
        int maxRetries)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Name is required."];
        }
        else if (name.Trim().Length > 128)
        {
            errors["name"] = ["Name cannot exceed 128 characters."];
        }

        if (string.IsNullOrWhiteSpace(configurationType) ||
            !(configurationType.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
              configurationType.Equals("embedding", StringComparison.OrdinalIgnoreCase)))
        {
            errors["configurationType"] = ["Configuration type must be chat or embedding."];
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            errors["baseUrl"] = ["BaseUrl is required."];
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            errors["model"] = ["Model is required."];
        }

        if (timeoutSeconds is < 1 or > 300)
        {
            errors["timeoutSeconds"] = ["Timeout must be between 1 and 300 seconds."];
        }

        if (maxRetries is < 0 or > 5)
        {
            errors["maxRetries"] = ["Max retries must be between 0 and 5."];
        }

        return errors.Count == 0 ? null : errors;
    }
}

public sealed record CreateModelConfigurationCommand(
    string Name,
    string Provider,
    string ConfigurationType,
    string BaseUrl,
    string Model,
    string? ApiKey,
    int TimeoutSeconds,
    int MaxRetries);

public sealed record UpdateModelConfigurationCommand(
    string Name,
    string Provider,
    string ConfigurationType,
    string BaseUrl,
    string Model,
    string? ApiKey,
    int TimeoutSeconds,
    int MaxRetries,
    int Version);

public sealed record CompatibilityModelConfigurationCommand(
    string Provider,
    string ConfigurationType,
    string BaseUrl,
    string Model,
    string? ApiKey,
    int TimeoutSeconds,
    int MaxRetries,
    bool IsEnabled,
    bool IsDefault);

public enum ModelConfigurationMutationStatus
{
    Success,
    Invalid,
    NotFound,
    NameConflict,
    ConcurrencyConflict,
    TestRequired,
    DefaultDisableForbidden,
    DefaultConflict,
    ProviderFailure
}

public sealed record ModelConfigurationMutationResult(
    ModelConfigurationMutationStatus Status,
    ModelConfigEntity? Entity = null,
    Dictionary<string, string[]>? Errors = null)
{
    public static ModelConfigurationMutationResult Succeeded(ModelConfigEntity entity) =>
        new(ModelConfigurationMutationStatus.Success, entity);

    public static ModelConfigurationMutationResult Invalid(Dictionary<string, string[]> errors) =>
        new(ModelConfigurationMutationStatus.Invalid, Errors: errors);

    public static ModelConfigurationMutationResult NotFound() =>
        new(ModelConfigurationMutationStatus.NotFound);

    public static ModelConfigurationMutationResult NameConflict() =>
        new(ModelConfigurationMutationStatus.NameConflict);

    public static ModelConfigurationMutationResult ConcurrencyConflict() =>
        new(ModelConfigurationMutationStatus.ConcurrencyConflict);

    public static ModelConfigurationMutationResult TestRequired() =>
        new(ModelConfigurationMutationStatus.TestRequired);

    public static ModelConfigurationMutationResult DefaultDisableForbidden() =>
        new(ModelConfigurationMutationStatus.DefaultDisableForbidden);

    public static ModelConfigurationMutationResult DefaultConflict() =>
        new(ModelConfigurationMutationStatus.DefaultConflict);

    public static ModelConfigurationMutationResult ProviderFailure(ModelConfigEntity entity) =>
        new(ModelConfigurationMutationStatus.ProviderFailure, entity);
}
