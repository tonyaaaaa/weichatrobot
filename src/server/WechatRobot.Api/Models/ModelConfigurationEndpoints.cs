using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Api.Models;

public static class ModelConfigurationEndpoints
{
    public static RouteGroupBuilder MapModelConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/model-configurations").RequireAuthorization(SystemRoles.Admin);
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("{id:guid}", UpdateByIdAsync);
        group.MapPost("{id:guid}/test-connection", TestConnectionByIdAsync);
        group.MapPost("{id:guid}/enabled", SetEnabledAsync);
        group.MapPost("{id:guid}/default", SetDefaultAsync);
        group.MapDelete("{id:guid}/api-key", ClearApiKeyAsync);
        group.MapDelete("{id:guid}", DeleteAsync);
        group.MapPut("{name}", UpsertAsync);
        group.MapPost("{name}/test-connection", TestConnectionAsync);
        return group;
    }

    private static async Task<IResult> CreateAsync(
        CreateModelConfigurationRequest request,
        ModelConfigurationManager manager,
        ModelConfigurationService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await manager.CreateAsync(
            new CreateModelConfigurationCommand(
                request.Name,
                request.Provider,
                request.ConfigurationType,
                request.BaseUrl,
                request.Model,
                request.ApiKey,
                request.TimeoutSeconds,
                request.MaxRetries),
            httpContext.User.Identity?.Name ?? "unknown",
            cancellationToken);

        return MapMutationResult(
            result,
            entity => Results.Created($"/api/admin/model-configurations/{entity.Id}", ToResponse(entity, service)));
    }

    private static async Task<IResult> UpdateByIdAsync(
        Guid id,
        UpdateModelConfigurationByIdRequest request,
        ModelConfigurationManager manager,
        ModelConfigurationService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await manager.UpdateAsync(
            id,
            new UpdateModelConfigurationCommand(
                request.Name,
                request.Provider,
                request.ConfigurationType,
                request.BaseUrl,
                request.Model,
                request.ApiKey,
                request.TimeoutSeconds,
                request.MaxRetries,
                request.Version),
            httpContext.User.Identity?.Name ?? "unknown",
            cancellationToken);

        return MapMutationResult(result, entity => Results.Ok(ToResponse(entity, service)));
    }

    private static async Task<IResult> TestConnectionByIdAsync(
        Guid id,
        ModelConfigurationManager manager,
        ModelConfigurationService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await manager.TestConnectionAsync(
            id,
            httpContext.User.Identity?.Name ?? "unknown",
            cancellationToken);
        return MapMutationResult(result, entity => Results.Ok(ToResponse(entity, service)), service);
    }

    private static async Task<IResult> SetEnabledAsync(
        Guid id,
        SetEnabledRequest request,
        ModelConfigurationManager manager,
        ModelConfigurationService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await manager.SetEnabledAsync(
            id,
            request.Enabled,
            request.Version,
            httpContext.User.Identity?.Name ?? "unknown",
            cancellationToken);
        return MapMutationResult(result, entity => Results.Ok(ToResponse(entity, service)));
    }

    private static async Task<IResult> SetDefaultAsync(
        Guid id,
        SetDefaultRequest request,
        ModelConfigurationManager manager,
        ModelConfigurationService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await manager.SetDefaultAsync(
            id,
            request.IsDefault,
            request.Version,
            httpContext.User.Identity?.Name ?? "unknown",
            cancellationToken);
        return MapMutationResult(result, entity => Results.Ok(ToResponse(entity, service)));
    }

    private static async Task<IResult> ClearApiKeyAsync(
        Guid id,
        int version,
        ModelConfigurationManager manager,
        ModelConfigurationService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await manager.ClearApiKeyAsync(
            id,
            version,
            httpContext.User.Identity?.Name ?? "unknown",
            cancellationToken);
        return MapMutationResult(result, entity => Results.Ok(ToResponse(entity, service)));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        int version,
        ModelConfigurationManager manager,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await manager.DeleteAsync(
            id,
            version,
            httpContext.User.Identity?.Name ?? "unknown",
            cancellationToken);
        return MapMutationResult(result, _ => Results.NoContent());
    }

    private static async Task<IResult> ListAsync(ModelConfigurationManager manager, ModelConfigurationService service, CancellationToken cancellationToken)
    {
        var configurations = await manager.ListAsync(cancellationToken);
        return Results.Ok(configurations.Select(config => ToResponse(config, service)));
    }

    private static async Task<IResult> UpsertAsync(
        string name,
        UpdateModelConfigurationRequest request,
        ModelConfigurationManager manager,
        ModelConfigurationService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await manager.UpsertCompatibilityAsync(
            name,
            new CompatibilityModelConfigurationCommand(
                request.Provider, request.ConfigurationType, request.BaseUrl, request.Model, request.ApiKey,
                request.TimeoutSeconds, request.MaxRetries, request.IsEnabled, request.IsDefault),
            httpContext.User.Identity?.Name ?? "unknown",
            cancellationToken);
        return MapMutationResult(result, entity => Results.Ok(ToResponse(entity, service)));
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

    private static IResult MapMutationResult(
        ModelConfigurationMutationResult result,
        Func<ModelConfigEntity, IResult> onSuccess,
        ModelConfigurationService? service = null) =>
        result.Status switch
        {
            ModelConfigurationMutationStatus.Success => onSuccess(result.Entity!),
            ModelConfigurationMutationStatus.Invalid => Results.ValidationProblem(result.Errors!),
            ModelConfigurationMutationStatus.NotFound => Results.NotFound(),
            ModelConfigurationMutationStatus.NameConflict => Results.Conflict(new
            {
                code = "model_name_conflict",
                message = "A model configuration with the same name already exists."
            }),
            ModelConfigurationMutationStatus.ConcurrencyConflict => Results.Conflict(new
            {
                code = "model_concurrency_conflict",
                message = "The model configuration was changed by another request."
            }),
            ModelConfigurationMutationStatus.TestRequired => Results.Conflict(new
            {
                code = "model_test_required",
                message = "A successful connection test is required for the current configuration."
            }),
            ModelConfigurationMutationStatus.DefaultDisableForbidden => Results.Conflict(new
            {
                code = "model_default_disable_forbidden",
                message = "Select another default model before disabling this configuration."
            }),
            ModelConfigurationMutationStatus.DefaultConflict => Results.Conflict(new
            {
                code = "model_default_conflict",
                message = "Another configuration became the default at the same time."
            }),
            ModelConfigurationMutationStatus.ProviderFailure when service is not null =>
                Results.Json(ToResponse(result.Entity!, service), statusCode: StatusCodes.Status502BadGateway),
            ModelConfigurationMutationStatus.DefaultDeleteBlocked => Results.Conflict(new
            {
                code = "model_default_delete_blocked",
                message = "Clear the default assignment before deleting this configuration."
            }),
            ModelConfigurationMutationStatus.ReferenceDeleteBlocked => Results.Conflict(new
            {
                code = "model_reference_delete_blocked",
                message = "The model configuration is referenced by retrieval audit history.",
                retrievalAuditCount = result.References!.RetrievalAuditCount
            }),
            _ => Results.Problem()
        };

    private static ModelConfigurationResponse ToResponse(ModelConfigEntity entity, ModelConfigurationService service)
    {
        var apiKey = service.GetApiKeyMetadata(entity.EncryptedApiKey);
        return new ModelConfigurationResponse(entity.Id, entity.Name, entity.Provider, entity.ConfigurationType, entity.BaseUrl, entity.Model,
            entity.TimeoutSeconds, entity.MaxRetries, entity.IsEnabled, entity.IsDefault, apiKey.HasApiKey, apiKey.LastFour,
            entity.ConnectionStatus, entity.LastTestedAtUtc, entity.LastTestFailureSummary, entity.Version);
    }

    private static ModelConfigurationRecord ToRecord(ModelConfigEntity entity) => new(entity.Id, entity.Name, entity.Provider, entity.BaseUrl,
        entity.Model, entity.EncryptedApiKey, entity.TimeoutSeconds, entity.MaxRetries, entity.IsEnabled, entity.IsDefault);

    public sealed record UpdateModelConfigurationRequest(string Provider, string ConfigurationType, string BaseUrl, string Model, string? ApiKey,
        int TimeoutSeconds, int MaxRetries, bool IsEnabled, bool IsDefault);
    public sealed record CreateModelConfigurationRequest(string Name, string Provider, string ConfigurationType, string BaseUrl, string Model,
        string? ApiKey, int TimeoutSeconds, int MaxRetries);
    public sealed record UpdateModelConfigurationByIdRequest(string Name, string Provider, string ConfigurationType, string BaseUrl, string Model,
        string? ApiKey, int TimeoutSeconds, int MaxRetries, int Version);
    public sealed record SetEnabledRequest(bool Enabled, int Version);
    public sealed record SetDefaultRequest(bool IsDefault, int Version);
    public sealed record ModelConfigurationResponse(Guid Id, string Name, string Provider, string ConfigurationType, string BaseUrl, string Model,
        int TimeoutSeconds, int MaxRetries, bool IsEnabled, bool IsDefault, bool HasApiKey, string? LastFour,
        string ConnectionStatus, DateTime? LastTestedAtUtc, string? LastTestFailureSummary, int Version);
    public sealed record ConnectionTestResponse(bool Succeeded);
}
