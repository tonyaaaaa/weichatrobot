using WechatRobot.Application.Knowledge;
using WechatRobot.KnowledgeVectorMigration;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class KnowledgeVectorMigrationPlannerTests
{
    [Fact]
    public void Planner_groups_versions_by_contract_without_requesting_embeddings()
    {
        var mappings = Enumerable.Range(0, 322).Select(_ => new ActiveVectorMapping(
            Guid.NewGuid(), Guid.NewGuid(), $"kb_cosine_3_g1_{Guid.NewGuid():N}", true,
            3, VectorDistance.Cosine, 1, "glm", "https://embedding.example.test/v1", "embedding-3")).ToArray();

        var plan = new KnowledgeVectorMigrationPlanner().Build(mappings);

        Assert.Single(plan.Destinations);
        Assert.Equal(322, plan.Versions.Count);
        Assert.All(plan.Versions, item => Assert.NotEqual(item.Source.SourceCollection, item.DestinationCollection));
    }

    [Fact]
    public void Any_point_mismatch_blocks_database_switch()
    {
        Assert.False(new KnowledgeVectorMigrationPlanner().CanSwitch(
            [new VersionVerification(5, 4, true)]));
    }

    [Fact]
    public void Matching_nonempty_verifications_allow_database_switch()
    {
        Assert.True(new KnowledgeVectorMigrationPlanner().CanSwitch(
            [new VersionVerification(5, 5, true), new VersionVerification(2, 2, true)]));
    }
}
