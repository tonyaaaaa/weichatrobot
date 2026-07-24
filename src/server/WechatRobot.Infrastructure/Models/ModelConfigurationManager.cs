using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Models;

public sealed class ModelConfigurationManager(
    WechatRobotDbContext database,
    ModelConfigurationService service,
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

        result.Entity!.IsEnabled = command.IsEnabled;
        result.Entity.IsDefault = command.IsDefault;
        await database.SaveChangesAsync(cancellationToken);
        return result;
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
    ConcurrencyConflict
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
}
