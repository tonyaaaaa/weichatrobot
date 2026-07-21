using WechatRobot.Application.Security;

namespace WechatRobot.Application.Models;

public sealed class ModelConfigurationService(ISecretProtector secretProtector)
{
    public string? ProtectSubmittedApiKey(string? submittedApiKey, string? existingEncryptedApiKey)
    {
        return string.IsNullOrWhiteSpace(submittedApiKey)
            ? existingEncryptedApiKey
            : secretProtector.Protect(submittedApiKey.Trim());
    }

    public ModelProviderConfiguration ToProviderConfiguration(ModelConfigurationRecord record)
    {
        return new ModelProviderConfiguration(
            record.BaseUrl,
            record.Model,
            record.EncryptedApiKey ?? throw new InvalidOperationException($"Model configuration '{record.Name}' has no API key."),
            TimeSpan.FromSeconds(record.TimeoutSeconds),
            record.MaxRetries);
    }

    public ApiKeyMetadata GetApiKeyMetadata(string? encryptedApiKey)
    {
        if (string.IsNullOrWhiteSpace(encryptedApiKey))
        {
            return new ApiKeyMetadata(false, null);
        }

        var plaintext = secretProtector.Unprotect(encryptedApiKey);
        return new ApiKeyMetadata(true, plaintext.Length <= 4 ? "****" : plaintext[^4..]);
    }
}

public sealed record ApiKeyMetadata(bool HasApiKey, string? LastFour);

public sealed record ModelConfigurationRecord(
    Guid Id,
    string Name,
    string Provider,
    string BaseUrl,
    string Model,
    string? EncryptedApiKey,
    int TimeoutSeconds,
    int MaxRetries,
    bool IsEnabled,
    bool IsDefault);
