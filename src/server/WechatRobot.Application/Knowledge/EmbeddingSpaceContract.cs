using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WechatRobot.Application.Knowledge;

public sealed partial record EmbeddingSpaceContract(
    string Key,
    string CollectionName,
    int Dimension,
    VectorDistance Distance)
{
    public static EmbeddingSpaceContract Create(
        string provider,
        string baseUrl,
        string model,
        int dimension,
        VectorDistance distance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var normalizedBaseUrl = baseUrl.Trim().TrimEnd('/').ToLowerInvariant();
        var normalizedModel = model.Trim();
        var identity = string.Join('\n', normalizedProvider, normalizedBaseUrl, normalizedModel, dimension, distance);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
        var normalizedDistance = distance.ToString().ToLowerInvariant();
        return new(
            $"{hash}:{normalizedDistance}:{dimension}",
            $"kb_shared_{hash}_{normalizedDistance}_{dimension}",
            dimension,
            distance);
    }

    public static bool IsSharedCollectionName(string? collectionName) =>
        collectionName is not null && SharedCollectionName().IsMatch(collectionName);

    [GeneratedRegex("^kb_shared_[0-9a-f]{16}_(cosine|dot|euclid)_[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SharedCollectionName();
}
