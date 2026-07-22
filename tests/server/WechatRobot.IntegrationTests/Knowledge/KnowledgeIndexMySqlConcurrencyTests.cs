using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeIndexMySqlConcurrencyTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;
    public KnowledgeIndexMySqlConcurrencyTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Concurrent_version_activation_has_one_winner_and_enqueues_old_cleanup_atomically()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).Options;
        await using (var setup = new WechatRobotDbContext(options))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var document = new KnowledgeDocumentEntity { Status = "active" };
            var oldVersion = Version(document.Id, 1, "active", true);
            oldVersion.IndexCollectionName = "kb_cosine_3"; oldVersion.EmbeddingDimension = 3; oldVersion.VectorDistance = "cosine";
            var second = Version(document.Id, 2, "approved", false);
            var third = Version(document.Id, 3, "approved", false);
            document.ActiveVersionId = oldVersion.Id;
            setup.AddRange(document, oldVersion, second, third);
            setup.KnowledgeIndexJobs.AddRange(Job(document.Id, second.Id, oldVersion.Id), Job(document.Id, third.Id, oldVersion.Id));
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        KnowledgeIndexWork Work(KnowledgeIndexJobEntity job) => new(job.Id, job.KnowledgeDocumentId, job.KnowledgeDocumentVersionId,
            job.PreviousActiveVersionId, "kb_cosine_3", 3, VectorDistance.Cosine, []);
        KnowledgeIndexJobEntity[] jobs;
        await using (var read = new WechatRobotDbContext(options)) jobs = await read.KnowledgeIndexJobs.AsNoTracking().Where(job => job.Operation == "index").ToArrayAsync(TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ActivateAsync(KnowledgeIndexJobEntity job)
        {
            await using var database = new WechatRobotDbContext(options);
            var service = Service(database);
            await gate.Task;
            return await service.ActivateVersionAsync(Work(job), TestContext.Current.CancellationToken);
        }
        var attempts = jobs.Select(ActivateAsync).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
        await using var verify = new WechatRobotDbContext(options);
        var storedDocument = await verify.KnowledgeDocuments.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(storedDocument.ActiveVersionId.HasValue);
        Assert.Contains(storedDocument.ActiveVersionId.Value, jobs.Select(job => job.KnowledgeDocumentVersionId));
        var published = await verify.KnowledgeDocumentVersions.AsNoTracking().Where(version => version.IsPublished).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(storedDocument.ActiveVersionId, Assert.Single(published).Id);
        var cleanup = await verify.KnowledgeIndexJobs.AsNoTracking().Where(job => job.Operation == "cleanup").ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Single(cleanup);
        Assert.Equal(1, await verify.KnowledgeIndexJobs.CountAsync(job => job.Status == "completed", TestContext.Current.CancellationToken));
    }

    private static QdrantKnowledgeService Service(WechatRobotDbContext database) => new(database,
        new ModelConfigurationService(new PassThroughProtector()), new KnowledgeIndexOptions(3, VectorDistance.Cosine), TimeProvider.System);

    private static KnowledgeIndexJobEntity Job(Guid documentId, Guid versionId, Guid oldVersionId) => new()
    {
        KnowledgeDocumentId = documentId, KnowledgeDocumentVersionId = versionId, PreviousActiveVersionId = oldVersionId,
        CollectionName = "kb_cosine_3", Dimension = 3, Distance = "cosine", Status = "leased", LeaseOwner = "test"
    };

    private static KnowledgeDocumentVersionEntity Version(Guid documentId, int number, string status, bool published) => new()
    {
        KnowledgeDocumentId = documentId, Version = number, OriginalFileName = $"v{number}.txt", SafeFileName = $"v{number}.txt",
        ContentType = "text/plain", Sha256 = number.ToString().PadLeft(64, '0'), ObjectKey = $"v{number}", Status = status, IsPublished = published
    };

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
