namespace WechatRobot.UnitTests.Knowledge;

public sealed class KnowledgeVectorMigrationMySqlQueryContractTests
{
    [Fact]
    public void Migration_runner_uses_provider_stable_guid_batch_predicates()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "tools",
            "WechatRobot.KnowledgeVectorMigration",
            "KnowledgeVectorMigrationRunner.cs"));

        Assert.Contains("GuidBatchQuery.CreateBatches(versionIds)", source, StringComparison.Ordinal);
        Assert.Contains("GuidBatchQuery.CreateBatches(modelIds)", source, StringComparison.Ordinal);
        Assert.Contains("MaxDegreeOfParallelism = 4", source, StringComparison.Ordinal);
        Assert.DoesNotContain("versionIds.Contains(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("modelIds.Contains(", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WechatRobot.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
