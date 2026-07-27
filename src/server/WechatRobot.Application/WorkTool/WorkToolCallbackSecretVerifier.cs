using System.Security.Cryptography;
using System.Text;

namespace WechatRobot.Application.WorkTool;

public static class WorkToolCallbackSecretVerifier
{
    public static bool Matches(
        string? submitted,
        string currentHash,
        string? previousHash,
        DateTime? previousExpiresAtUtc,
        DateTime nowUtc)
    {
        if (MatchesHash(submitted, currentHash))
        {
            return true;
        }

        return previousExpiresAtUtc >= nowUtc &&
               MatchesHash(submitted, previousHash);
    }

    private static bool MatchesHash(string? submitted, string? configuredHash)
    {
        if (string.IsNullOrWhiteSpace(submitted) ||
            string.IsNullOrWhiteSpace(configuredHash))
        {
            return false;
        }

        try
        {
            var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(submitted));
            var expectedHash = Convert.FromHexString(configuredHash);
            return expectedHash.Length == submittedHash.Length &&
                   CryptographicOperations.FixedTimeEquals(submittedHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
