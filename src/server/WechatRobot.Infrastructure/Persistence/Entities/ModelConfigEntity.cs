namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class ModelConfigEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ConfigurationType { get; set; } = string.Empty;
    public string? DefaultConfigurationType { get; private set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int? EmbeddingDimension { get; set; }
    public string WebSearchMode { get; set; } = "None";
    public string? EncryptedApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public string ConnectionStatus { get; set; } = ModelConnectionStatus.Untested;
    public DateTime? LastTestedAtUtc { get; set; }
    public string? LastTestFailureSummary { get; set; }
    public string? TestedConfigurationFingerprint { get; set; }
    public int ApiKeyVersion { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class ModelConnectionStatus
{
    public const string Untested = "Untested";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}
