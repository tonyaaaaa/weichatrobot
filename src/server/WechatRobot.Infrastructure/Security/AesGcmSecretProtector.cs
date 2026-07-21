using System.Security.Cryptography;
using System.Text;
using WechatRobot.Application.Security;

namespace WechatRobot.Infrastructure.Security;

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[] _masterKey;

    public AesGcmSecretProtector()
    {
        var encoded = Environment.GetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64");
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException("WECHATROBOT_MASTER_KEY_BASE64 must be set to a Base64 encoded 32-byte key.");
        }

        try
        {
            _masterKey = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("WECHATROBOT_MASTER_KEY_BASE64 must be valid Base64.", exception);
        }

        if (_masterKey.Length != 32)
        {
            throw new InvalidOperationException("WECHATROBOT_MASTER_KEY_BASE64 must decode to exactly 32 bytes.");
        }
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];
        using var aes = new AesGcm(_masterKey, TagLength);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        return string.Join(':', Version, Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        var parts = protectedValue.Split(':');
        if (parts.Length != 4 || parts[0] != Version)
        {
            throw new CryptographicException("Unsupported protected secret format.");
        }

        try
        {
            var nonce = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var ciphertext = Convert.FromBase64String(parts[3]);
            if (nonce.Length != NonceLength || tag.Length != TagLength)
            {
                throw new CryptographicException("Invalid protected secret format.");
            }

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(_masterKey, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Invalid protected secret format.", exception);
        }
    }
}
