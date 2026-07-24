using WechatRobot.Application.Models;
using WechatRobot.Application.Security;

namespace WechatRobot.UnitTests.Models;

public sealed class ModelConfigurationServiceTests
{
    private readonly ModelConfigurationService service = new(new ThrowingProtector());

    [Fact]
    public void Fingerprint_changes_when_api_key_version_changes()
    {
        var record = Record();

        var first = service.ComputeFingerprint(record, "chat", 1);
        var second = service.ComputeFingerprint(record, "chat", 2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Fingerprint_changes_when_configuration_type_changes()
    {
        var record = Record();

        var chat = service.ComputeFingerprint(record, "chat", 1);
        var embedding = service.ComputeFingerprint(record, "embedding", 1);

        Assert.NotEqual(chat, embedding);
    }

    [Fact]
    public void Clear_api_key_returns_null_without_decrypting_existing_value()
    {
        Assert.Null(service.ClearApiKey("encrypted-value"));
    }

    private static ModelConfigurationRecord Record() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Local",
        "OpenAI compatible",
        "https://local.test/",
        "model-a",
        "encrypted-value",
        30,
        0,
        false,
        false);

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string Protect(string plaintext) =>
            throw new InvalidOperationException("Protect should not be called.");

        public string Unprotect(string protectedValue) =>
            throw new InvalidOperationException("Unprotect should not be called.");
    }
}
