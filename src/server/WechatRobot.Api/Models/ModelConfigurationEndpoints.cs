using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Api.Models;

public static class ModelConfigurationEndpoints
{
    public static RouteGroupBuilder MapModelConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/model-configurations").RequireAuthorization(SystemRoles.Admin);
        group.MapGet("", ListAsync);
        group.MapPut("{name}", UpsertAsync);
        group.MapPost("{name}/test-connection", TestConnectionAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(WechatRobotDbContext database, ModelConfigurationService service, CancellationToken cancellationToken)
    {
        var configurations = await database.ModelConfigs.AsNoTracking().OrderBy(config => config.Name).ToListAsync(cancellationToken);
        return Results.Ok(configurations.Select(config => ToResponse(config, service)));
    }

    private static async Task<IResult> UpsertAsync(
        string name,
        UpdateModelConfigurationRequest request,
        WechatRobotDbContext database,
        ModelConfigurationService service,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedType(request.ConfigurationType) || string.IsNullOrWhiteSpace(request.BaseUrl) || string.IsNullOrWhiteSpace(request.Model) ||
            request.TimeoutSeconds is < 1 or > 300 || request.MaxRetries is < 0 or > 5)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = ["Configuration type, BaseUrl, model, timeout, or retry values are invalid."] });
        }

        var config = await database.ModelConfigs.SingleOrDefaultAsync(item => item.Name == name, cancellationToken);
        if (config is null)
        {
            config = new ModelConfigEntity { Name = name };
            database.ModelConfigs.Add(config);
        }

        config.Provider = request.Provider.Trim();
        config.ConfigurationType = request.ConfigurationType.Trim().ToLowerInvariant();
        config.BaseUrl = request.BaseUrl.TrimEnd('/');
        config.Model = request.Model.Trim();
        config.EncryptedApiKey = service.ProtectSubmittedApiKey(request.ApiKey, config.EncryptedApiKey);
        config.TimeoutSeconds = request.TimeoutSeconds;
        config.MaxRetries = request.MaxRetries;
        config.IsEnabled = request.IsEnabled;
        config.IsDefault = request.IsDefault;
        config.UpdatedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(config, service));
    }

    private static async Task<IResult> TestConnectionAsync(
        string name,
        WechatRobotDbContext database,
        ModelConfigurationService service,
        IChatCompletionClient chatClient,
        IEmbeddingClient embeddingClient,
        CancellationToken cancellationToken)
    {
        var entity = await database.ModelConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.Name == name, cancellationToken);
        if (entity is null)
        {
            return Results.NotFound();
        }

        try
        {
            var configuration = service.ToProviderConfiguration(ToRecord(entity));
            if (entity.ConfigurationType.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                await chatClient.CompleteAsync(configuration, new ChatCompletionRequest([new ChatMessage("user", "connection test")]), cancellationToken);
            }
            else if (entity.ConfigurationType.Equals("embedding", StringComparison.OrdinalIgnoreCase))
            {
                await embeddingClient.CreateEmbeddingsAsync(configuration, new EmbeddingBatchRequest(["connection test"]), cancellationToken);
            }
            else
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["configurationType"] = ["Configuration type must be chat or embedding."] });
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("Provider connection test failed.", statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new ConnectionTestResponse(true));
    }

    private static bool IsSupportedType(string value) => value.Equals("chat", StringComparison.OrdinalIgnoreCase) || value.Equals("embedding", StringComparison.OrdinalIgnoreCase);

    private static ModelConfigurationResponse ToResponse(ModelConfigEntity entity, ModelConfigurationService service)
    {
        var apiKey = service.GetApiKeyMetadata(entity.EncryptedApiKey);
        return new ModelConfigurationResponse(entity.Id, entity.Name, entity.Provider, entity.ConfigurationType, entity.BaseUrl, entity.Model,
            entity.TimeoutSeconds, entity.MaxRetries, entity.IsEnabled, entity.IsDefault, apiKey.HasApiKey, apiKey.LastFour);
    }

    private static ModelConfigurationRecord ToRecord(ModelConfigEntity entity) => new(entity.Id, entity.Name, entity.Provider, entity.BaseUrl,
        entity.Model, entity.EncryptedApiKey, entity.TimeoutSeconds, entity.MaxRetries, entity.IsEnabled, entity.IsDefault);

    public sealed record UpdateModelConfigurationRequest(string Provider, string ConfigurationType, string BaseUrl, string Model, string? ApiKey,
        int TimeoutSeconds, int MaxRetries, bool IsEnabled, bool IsDefault);
    public sealed record ModelConfigurationResponse(Guid Id, string Name, string Provider, string ConfigurationType, string BaseUrl, string Model,
        int TimeoutSeconds, int MaxRetries, bool IsEnabled, bool IsDefault, bool HasApiKey, string? LastFour);
    public sealed record ConnectionTestResponse(bool Succeeded);
}
