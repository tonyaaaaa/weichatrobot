using System.Text.Json;

namespace WechatRobot.ContractTests.Configuration;

public sealed class WorkerConversationConfigurationDocumentationTests
{
    [Fact]
    public void Conversation_runtime_settings_are_documented_without_changing_defaults()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigurationPath()));
        var root = document.RootElement;

        var guide = root.GetProperty("_configurationGuide");
        Assert.False(string.IsNullOrWhiteSpace(guide.GetProperty("Purpose").GetString()));
        Assert.Contains("Worker", guide.GetProperty("Activation").GetString(), StringComparison.Ordinal);
        Assert.Contains("环境变量", guide.GetProperty("SecretPolicy").GetString(), StringComparison.Ordinal);

        var grounded = root.GetProperty("GroundedAnswer");
        AssertDocumented(grounded, "ConfidenceThreshold");
        AssertDocumented(grounded, "MaximumEvidence");
        AssertDocumented(grounded, "InsufficientEvidenceText");
        AssertDocumented(grounded, "SystemFailureText");
        AssertDocumented(grounded, "SensitiveHandoffText");
        AssertDocumented(grounded, "SensitiveTerms");
        Assert.Equal(.7, grounded.GetProperty("ConfidenceThreshold").GetDouble());
        Assert.Equal(8, grounded.GetProperty("MaximumEvidence").GetInt32());

        var retrieval = root.GetProperty("RetrievalQuery");
        AssertDocumented(retrieval, "TokenCap");
        Assert.Equal(512, retrieval.GetProperty("TokenCap").GetInt32());

        var summary = root.GetProperty("ConversationSummary");
        AssertDocumented(summary, "MaxInputTokens");
        AssertDocumented(summary, "MaxOutputCharacters");
        Assert.Equal(512, summary.GetProperty("MaxInputTokens").GetInt32());
        Assert.Equal(1200, summary.GetProperty("MaxOutputCharacters").GetInt32());
    }

    private static void AssertDocumented(JsonElement section, string propertyName)
    {
        Assert.False(string.IsNullOrWhiteSpace(section.GetProperty($"_{propertyName}Comment").GetString()));
    }

    private static string ConfigurationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        return Path.Combine(root, "src", "server", "WechatRobot.Worker", "appsettings.json");
    }
}
