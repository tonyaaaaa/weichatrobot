using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
            record.EncryptedApiKey,
            TimeSpan.FromSeconds(record.TimeoutSeconds),
            record.MaxRetries,
            record.WebSearchMode);
    }

    public string ComputeFingerprint(ModelConfigurationRecord record, string configurationType, int apiKeyVersion)
    {
        var canonical = string.Join(
            '\n',
            configurationType.Trim().ToUpperInvariant(),
            record.Provider.Trim().ToUpperInvariant(),
            record.BaseUrl.TrimEnd('/').ToUpperInvariant(),
            record.Model.Trim(),
            record.EmbeddingDimension?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            record.WebSearchMode.Trim().ToUpperInvariant(),
            apiKeyVersion.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public string? ClearApiKey(string? existingEncryptedApiKey) => null;

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
    bool IsDefault,
    int? EmbeddingDimension = null,
    string WebSearchMode = "None");
