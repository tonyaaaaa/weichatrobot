using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Storage;
using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.Infrastructure.Health;

public enum ComponentHealthState
{
    Healthy,
    Failed
}

public sealed record ComponentHealthResult(
    string Name,
    ComponentHealthState State,
    bool Required,
    string? Detail = null);

public interface IComponentHealthProbe
{
    string Name { get; }
    bool Required { get; }
    Task<ComponentHealthResult> CheckAsync(CancellationToken cancellationToken);
}

public abstract class ComponentHealthProbe(string name, bool required) : IComponentHealthProbe
{
    public string Name { get; } = name;
    public bool Required { get; } = required;

    public async Task<ComponentHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await IsHealthyAsync(cancellationToken)
                ? new(Name, ComponentHealthState.Healthy, Required)
                : new(Name, ComponentHealthState.Failed, Required, "unavailable");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(Name, ComponentHealthState.Failed, Required, "unavailable");
        }
    }

    protected abstract Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

public sealed class MySqlHealthProbe(IDbContextFactory<WechatRobotDbContext> databaseFactory)
    : ComponentHealthProbe("MySQL", required: true)
{
    protected override async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
        return true;
    }
}

public sealed class QdrantHealthProbe(IHttpClientFactory clients)
    : ComponentHealthProbe("Qdrant", required: true)
{
    protected override async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(HealthServiceRegistration.QdrantClient)
            .GetAsync("readyz", cancellationToken);
        return response.IsSuccessStatusCode;
    }
}

public sealed class OcrHealthProbe(IConfiguration configuration)
    : ComponentHealthProbe("OCR", required: false)
{
    protected override Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(
            configuration["Ocr:Provider"] == "Aliyun" &&
            configuration["Ocr:Action"] == "RecognizeGeneral" &&
            !string.IsNullOrWhiteSpace(configuration["Ocr:Endpoint"]) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AliyunOcrOptions.AccessKeyIdEnvironmentVariable)) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AliyunOcrOptions.AccessKeySecretEnvironmentVariable)));
}

public sealed class OssConfigurationHealthProbe(IConfiguration configuration)
    : ComponentHealthProbe("OSS configuration", required: true)
{
    protected override Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var loopback = configuration["ObjectStorage:Provider"]?.Equals("loopback", StringComparison.OrdinalIgnoreCase) == true;
        if (loopback)
        {
            return Task.FromResult(
                Uri.TryCreate(
                    configuration[$"{LoopbackObjectStorageOptions.SectionName}:BaseUrl"],
                    UriKind.Absolute,
                    out var loopbackBaseUrl) &&
                LoopbackHttpPolicy.IsStrictLoopbackHttp(loopbackBaseUrl));
        }

        var required = new[]
        {
            configuration[$"{OssOptions.SectionName}:AccessKeyId"],
            configuration[$"{OssOptions.SectionName}:AccessKeySecret"],
            configuration[$"{OssOptions.SectionName}:Bucket"],
            configuration[$"{OssOptions.SectionName}:Endpoint"]
        };
        var configuredPublicBaseUrl = configuration[$"{OssOptions.SectionName}:PublicBaseUrl"];
        var publicBaseUrlIsHttps = string.IsNullOrWhiteSpace(configuredPublicBaseUrl) ||
            Uri.TryCreate(configuredPublicBaseUrl, UriKind.Absolute, out var publicBaseUrl) &&
            publicBaseUrl.Scheme == Uri.UriSchemeHttps;
        var riskAccepted = configuration.GetValue<bool>($"{OssOptions.SectionName}:PublicReadRiskAccepted");
        return Task.FromResult(
            required.All(value => !string.IsNullOrWhiteSpace(value)) &&
            publicBaseUrlIsHttps &&
            riskAccepted);
    }
}

public sealed class WorkerHeartbeatHealthProbe(
    IDbContextFactory<WechatRobotDbContext> databaseFactory,
    TimeProvider timeProvider,
    IConfiguration configuration)
    : IComponentHealthProbe
{
    public string Name => "Worker heartbeat";
    public bool Required => true;

    public async Task<ComponentHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var staleAfter = TimeSpan.FromSeconds(configuration.GetValue("Health:WorkerStaleAfterSeconds", 45));
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var lastSeen = await database.WorkerHeartbeats.AsNoTracking()
                .Where(value => value.Name == WorkerHeartbeatService.HeartbeatName)
                .Select(value => (DateTime?)value.LastSeenAtUtc)
                .SingleOrDefaultAsync(cancellationToken);
            if (!lastSeen.HasValue)
                return new(Name, ComponentHealthState.Failed, Required, "never reported");
            return timeProvider.GetUtcNow().UtcDateTime - lastSeen.Value <= staleAfter
                ? new(Name, ComponentHealthState.Healthy, Required)
                : new(Name, ComponentHealthState.Failed, Required, "stale");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(Name, ComponentHealthState.Failed, Required, "unavailable");
        }
    }
}

public static class HealthServiceRegistration
{
    public const string QdrantClient = "health-qdrant";

    public static IServiceCollection AddWechatRobotHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IComponentHealthProbe, MySqlHealthProbe>();
        services.AddScoped<IComponentHealthProbe, QdrantHealthProbe>();
        services.AddScoped<IComponentHealthProbe, OcrHealthProbe>();
        services.AddScoped<IComponentHealthProbe, OssConfigurationHealthProbe>();
        services.AddScoped<IComponentHealthProbe, WorkerHeartbeatHealthProbe>();
        services.AddHttpClient(QdrantClient, client =>
        {
            client.BaseAddress = new Uri(configuration["Qdrant:BaseUrl"] ?? "http://127.0.0.1:6333/");
            client.Timeout = TimeSpan.FromSeconds(3);
            var key = configuration["Qdrant:ApiKey"];
            if (!string.IsNullOrWhiteSpace(key)) client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", key);
        });
        return services;
    }
}
