using WechatRobot.Application.Knowledge;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class EmbeddingSpaceContractTests
{
    [Fact]
    public void Equivalent_semantic_settings_produce_the_same_contract()
    {
        var first = EmbeddingSpaceContract.Create(
            "GLM",
            "https://embedding.example.test/v1/",
            "embedding-3",
            1024,
            VectorDistance.Cosine);
        var second = EmbeddingSpaceContract.Create(
            " glm ",
            "https://embedding.example.test/v1",
            " embedding-3 ",
            1024,
            VectorDistance.Cosine);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal(first.CollectionName, second.CollectionName);
        Assert.True(EmbeddingSpaceContract.IsSharedCollectionName(first.CollectionName));
    }

    [Fact]
    public void Different_models_do_not_share_a_contract()
    {
        var first = EmbeddingSpaceContract.Create(
            "glm",
            "https://embedding.example.test/v1",
            "embedding-3",
            1024,
            VectorDistance.Cosine);
        var second = EmbeddingSpaceContract.Create(
            "glm",
            "https://embedding.example.test/v1",
            "embedding-4",
            1024,
            VectorDistance.Cosine);

        Assert.NotEqual(first.Key, second.Key);
        Assert.NotEqual(first.CollectionName, second.CollectionName);
    }

    [Fact]
    public void Collection_name_contains_only_safe_bounded_components()
    {
        var contract = EmbeddingSpaceContract.Create(
            "provider/with spaces",
            "https://embedding.example.test/secret-looking-path",
            "model:name",
            1536,
            VectorDistance.Dot);

        Assert.Matches("^kb_shared_[0-9a-f]{16}_dot_1536$", contract.CollectionName);
        Assert.True(contract.CollectionName.Length <= 128);
        Assert.DoesNotContain("provider", contract.CollectionName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", contract.CollectionName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Knowledge_entities_persist_bounded_embedding_contract_keys()
    {
        using var database = new WechatRobotDbContext(
            new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);

        Assert.Equal(96, database.Model.FindEntityType(typeof(KnowledgeDocumentEntity))!
            .FindProperty(nameof(KnowledgeDocumentEntity.ActiveEmbeddingContractKey))!.GetMaxLength());
        Assert.Equal(96, database.Model.FindEntityType(typeof(KnowledgeDocumentVersionEntity))!
            .FindProperty(nameof(KnowledgeDocumentVersionEntity.IndexEmbeddingContractKey))!.GetMaxLength());
        var job = database.Model.FindEntityType(typeof(KnowledgeIndexJobEntity))!;
        Assert.Equal(96, job.FindProperty(nameof(KnowledgeIndexJobEntity.EmbeddingContractKey))!.GetMaxLength());
        Assert.Equal(96, job.FindProperty(nameof(KnowledgeIndexJobEntity.PreviousActiveEmbeddingContractKey))!.GetMaxLength());
    }
}
