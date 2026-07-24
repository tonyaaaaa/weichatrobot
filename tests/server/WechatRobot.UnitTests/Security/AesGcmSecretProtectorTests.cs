using System.Security.Cryptography;
using WechatRobot.Infrastructure.Security;

namespace WechatRobot.UnitTests.Security;

[Collection("EnvironmentVariables")]
public sealed class AesGcmSecretProtectorTests
{
    [Fact]
    public void Protect_then_unprotect_round_trips_plaintext_with_versioned_ciphertext()
    {
        using var masterKey = MasterKeyScope.WithValidKey();
        var protector = new AesGcmSecretProtector();

        var encrypted = protector.Protect("provider-api-key");

        Assert.StartsWith("v1:", encrypted, StringComparison.Ordinal);
        Assert.Equal("provider-api-key", protector.Unprotect(encrypted));
    }

    [Fact]
    public void Protect_uses_a_unique_nonce_for_each_encryption()
    {
        using var masterKey = MasterKeyScope.WithValidKey();
        var protector = new AesGcmSecretProtector();

        var first = protector.Protect("same-key");
        var second = protector.Protect("same-key");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first.Split(':')[1], second.Split(':')[1]);
    }

    [Fact]
    public void Unprotect_rejects_tampered_ciphertext()
    {
        using var masterKey = MasterKeyScope.WithValidKey();
        var protector = new AesGcmSecretProtector();
        var parts = protector.Protect("provider-api-key").Split(':');
        var ciphertext = Convert.FromBase64String(parts[3]);
        ciphertext[0] ^= 0x01;
        parts[3] = Convert.ToBase64String(ciphertext);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(string.Join(':', parts)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("c2hvcnQ=")]
    public void Constructor_fails_fast_when_master_key_is_missing_or_not_32_bytes(string? value)
    {
        using var masterKey = MasterKeyScope.With(value);

        var exception = Assert.Throws<InvalidOperationException>(() => new AesGcmSecretProtector());

        Assert.Contains("WECHATROBOT_MASTER_KEY_BASE64", exception.Message, StringComparison.Ordinal);
    }

    private sealed class MasterKeyScope : IDisposable
    {
        private readonly string? _previous;

        private MasterKeyScope(string? value)
        {
            _previous = Environment.GetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64");
            Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", value);
        }

        public static MasterKeyScope WithValidKey() => With(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        public static MasterKeyScope With(string? value) => new(value);

        public void Dispose() => Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", _previous);
    }
}
