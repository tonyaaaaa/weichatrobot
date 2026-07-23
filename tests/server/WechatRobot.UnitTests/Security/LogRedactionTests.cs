using Microsoft.Extensions.Logging;
using System.Text.Json;
using WechatRobot.Infrastructure.Logging;

namespace WechatRobot.UnitTests.Security;

public sealed class LogRedactionTests
{
    public static TheoryData<string, string> SensitiveValues => new()
    {
        { "apiKey=sk-test-secret", "sk-test-secret" },
        { "token=callback-token-secret", "callback-token-secret" },
        { "robotId=worktool-robot-123", "worktool-robot-123" },
        { "AccessKeySecret=oss-secret-value", "oss-secret-value" },
        { "Authorization: Bearer jwt-secret-value", "jwt-secret-value" },
        { "ciphertext=AQIDBAUGBwgJCgsMDQ4PEA==", "AQIDBAUGBwgJCgsMDQ4PEA==" },
        { """{"password":"json-password-secret"}""", "json-password-secret" }
    };

    [Theory]
    [MemberData(nameof(SensitiveValues))]
    public void RedactMessage_removes_sensitive_values(string message, string secret)
    {
        var redacted = RedactionEnricher.RedactMessage(message);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("callbackToken")]
    [InlineData("workToolRobotId")]
    [InlineData("Oss:AccessKeySecret")]
    [InlineData("Authorization")]
    [InlineData("encryptedCiphertext")]
    [InlineData("Jwt:SigningKey")]
    [InlineData("password")]
    public void RedactValue_masks_sensitive_structured_properties(string propertyName)
    {
        Assert.Equal("[REDACTED]", RedactionEnricher.RedactValue(propertyName, "plain-secret"));
    }

    [Fact]
    public void RedactValue_preserves_non_sensitive_operational_values()
    {
        Assert.Equal("healthy", RedactionEnricher.RedactValue("status", "healthy"));
        Assert.Equal("512", RedactionEnricher.RedactValue("tokenCap", "512"));
        Assert.Equal("17", RedactionEnricher.RedactValue("tokenCount", "17"));
    }

    [Fact]
    public void Real_logger_redacts_structured_state_message_exception_and_scope()
    {
        using var output = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(output);
        using var factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(provider));
        var logger = factory.CreateLogger("redaction-test");
        using (logger.BeginScope(new Dictionary<string, object?> { ["Authorization"] = "Bearer scope-secret" }))
        {
            logger.LogError(
                new InvalidOperationException("""provider failed with {"ciphertext":"exception-secret"}"""),
                """payload {Payload} headers Authorization={Authorization} robot={WorkToolRobotId} count={TokenCount}""",
                """{"token":"json-secret"}""",
                "Bearer header-secret",
                "robot-secret",
                42);
        }

        var captured = output.ToString();
        foreach (var secret in new[] { "scope-secret", "exception-secret", "json-secret", "header-secret", "robot-secret" })
            Assert.DoesNotContain(secret, captured, StringComparison.Ordinal);
        Assert.Contains("\"TokenCount\":\"42\"", captured, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", captured, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactMessage_recursively_scrubs_escaped_json_and_oss_signed_urls()
    {
        const string message = """
            {"token":"prefix-\"escaped-secret-remainder","nested":{"OSSAccessKeyId":"oss-access-id","tokenCount":19,"url":"https://bucket.example.test/a?OSSAccessKeyId=query-id&Signature=query-signature&x-oss-security-token=query-token&partNumber=1"}}
            """;

        var redacted = RedactionEnricher.RedactMessage(message);

        foreach (var secret in new[] { "escaped-secret-remainder", "oss-access-id", "query-id", "query-signature", "query-token" })
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("\"tokenCount\":19", redacted, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(redacted);
        Assert.Contains("partNumber=1", json.RootElement.GetProperty("nested").GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Real_logger_scrubs_nested_json_strings_and_signed_url_query_parameters()
    {
        using var output = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(output);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("signed-url-test");
        var nestedJson = """{"payload":"{\"ciphertext\":\"escaped-cipher-secret\"}","url":"https://bucket.example.test/object?OSSAccessKeyId=oss-query-id&Signature=oss-signature"}""";

        logger.LogWarning("provider payload {Payload} {TokenCap}", nestedJson, 512);

        var captured = output.ToString();
        foreach (var secret in new[] { "escaped-cipher-secret", "oss-query-id", "oss-signature" })
            Assert.DoesNotContain(secret, captured, StringComparison.Ordinal);
        Assert.Contains("\"TokenCap\":\"512\"", captured, StringComparison.Ordinal);
    }
}
