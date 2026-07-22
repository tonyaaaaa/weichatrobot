using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Handoffs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Conversations;
using WechatRobot.Application.Conversations;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class HumanAnswerReviewTests : IClassFixture<MySqlFixture>, IAsyncLifetime
{
    private readonly MySqlFixture _fixture;
    private readonly IContainer _qdrant = new ContainerBuilder("qdrant/qdrant:v1.18.2").WithPortBinding(6333, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(x => x.ForPort(6333).ForPath("/readyz"))).Build();
    private HttpClient _qdrantHttp = null!;
    public HumanAnswerReviewTests(MySqlFixture fixture) => _fixture = fixture;
    public async ValueTask InitializeAsync() { await _qdrant.StartAsync(); _qdrantHttp = new() { BaseAddress = new Uri($"http://127.0.0.1:{_qdrant.GetMappedPublicPort(6333)}") }; }
    public async ValueTask DisposeAsync() { _qdrantHttp?.Dispose(); await _qdrant.DisposeAsync(); }
    [Fact]
    public async Task Approved_answer_is_only_published_after_its_index_generation_becomes_active()
    {
        await using var db = Database();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var tag = new KnowledgeTagEntity { Name = "售后", NormalizedName = "售后", IsEnabled = true };
        var reviewer = Guid.NewGuid();
        var reviewerUser = new ApplicationUser { Id = reviewer, UserName = "reviewer-" + reviewer.ToString("N"), NormalizedUserName = ("reviewer-" + reviewer.ToString("N")).ToUpperInvariant(),
            Email = reviewer + "@example.test", NormalizedEmail = (reviewer + "@example.test").ToUpperInvariant(), SecurityStamp = Guid.NewGuid().ToString() };
        var robot = new RobotConfigEntity { Name = "review-" + Guid.NewGuid().ToString("N"), WorkToolRobotId = Guid.NewGuid().ToString("N"), CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = Guid.NewGuid().ToString("N"), Name = "技术部" };
        var question = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name, SenderDisplayName = "客户",
            Text = "如何申请售后？", FallbackHash = Guid.NewGuid().ToString("N") };
        var embeddingConfig = new ModelConfigEntity { Name = "candidate-embedding-" + Guid.NewGuid().ToString("N"), Provider = "fake", ConfigurationType = "embedding",
            BaseUrl = "https://fake.test", Model = "fake", EncryptedApiKey = "fake", TimeoutSeconds = 5, MaxRetries = 0, IsEnabled = true, IsDefault = true };
        db.AddRange(tag, reviewerUser, robot, group, question, embeddingConfig);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var handoffService = new HandoffService(new EfHandoffStore(db), TimeProvider.System);
        var started = await handoffService.StartAsync(new(question.Id, robot.Id, group.Id, robot.WorkToolRobotId, group.Name, "explicit_transfer", "[]",
            HandoffPauseScope.Group, null, reviewer, reviewerUser.UserName!, "semantic-transfer"), TestContext.Current.CancellationToken);
        var resolved = await handoffService.ResolveAsync(started.Id, reviewer, "请提交订单号。", started.Version, TestContext.Current.CancellationToken);
        var candidate = await db.KnowledgeCandidates.SingleAsync(x => x.Id == resolved.Id, TestContext.Current.CancellationToken);
        var options = new KnowledgeIndexOptions(3, VectorDistance.Cosine);
        var knowledge = new QdrantKnowledgeService(db, new ModelConfigurationService(new FakeProtector()), options, TimeProvider.System);
        var service = new KnowledgeCandidateService(new EfKnowledgeCandidateStore(db), TimeProvider.System);

        var approved = await service.ReviewAsync(new(candidate.Id, reviewer, "approve", [tag.Id], null, "review-1", 0), TestContext.Current.CancellationToken);
        var committedOutbox = await db.DurableJobs.SingleAsync(x => x.Id == candidate.Id, TestContext.Current.CancellationToken);
        db.DurableJobs.Remove(committedOutbox);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var duplicate = await service.ReviewAsync(new(candidate.Id, reviewer, "approve", [tag.Id], null, "review-1", 0), TestContext.Current.CancellationToken);

        Assert.Equal(approved.PublishJobId, duplicate.PublishJobId);
        Assert.Equal("approved_pending_index", approved.Status);
        Assert.Empty(await db.KnowledgeIndexJobs.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        var publishJob = Assert.Single(await db.DurableJobs.AsNoTracking().Where(x => x.JobType == "PublishKnowledgeCandidate").ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.False((await db.KnowledgeCandidates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).PublishedAtUtc.HasValue);
        var chunk = await db.KnowledgeChunks.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("如何申请售后？", chunk.Question);
        Assert.Equal("请提交订单号。", chunk.Answer);
        Assert.Equal("approved", chunk.Status);

        var durable = new DurableJobRepository(db);
        var leasedPublish = await durable.LeaseNextJobAsync("PublishKnowledgeCandidate", "publisher", DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.NotNull(leasedPublish);
        await new KnowledgeCandidatePublishProcessor(db, knowledge).ProcessAsync(leasedPublish!, TestContext.Current.CancellationToken);
        await durable.CompleteJobAsync(publishJob.Id, "publisher", DateTime.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal("indexing", (await db.KnowledgeCandidates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Status);

        var leased = await knowledge.LeaseNextAsync("test", DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.NotNull(leased);
        var vectors = new QdrantVectorStore(_qdrantHttp);
        var embeddings = new FakeEmbeddingClient();
        await new KnowledgeIndexService(embeddings, vectors, knowledge, options).IndexAsync(leased!.Id, TestContext.Current.CancellationToken);

        var published = await db.KnowledgeCandidates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("published", published.Status);
        Assert.NotNull(published.PublishedAtUtc);
        var retrieval = new KnowledgeRetrievalEvidenceProvider(db, knowledge, embeddings, vectors);
        var evidence = await retrieval.RetrieveAsync("售后申请需要提供什么？", [tag.Id], 3, TestContext.Current.CancellationToken);
        Assert.Contains("请提交订单号。", Assert.Single(evidence).Text);
        var grounded = await new GroundedAnswerService(retrieval, new EvidenceAnswerChat(), new GroundedAnswerOptions(.1), new AnswerOutputFirewall())
            .AnswerAsync(new(Guid.NewGuid(), group.Id, "group", "售后申请需要提供什么？", [tag.Id], new([], null, false, false, [], 0),
                new(false, 3, 30, 3000, false, false), new("https://fake.test", "fake", "fake", TimeSpan.FromSeconds(1), 0)), TestContext.Current.CancellationToken);
        Assert.Equal(AnswerDecisionKind.Answer, grounded.Decision.Kind);
        Assert.Equal("请提交订单号。", grounded.Decision.GroupText);
        await vectors.DeleteCollectionAsync(new(published.KnowledgeDocumentVersionId is null ? "unused" :
            (await db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(x => x.Id == published.KnowledgeDocumentVersionId, TestContext.Current.CancellationToken)).IndexCollectionName!, 3, VectorDistance.Cosine),
            TestContext.Current.CancellationToken);

        await VerifyRelationalHandoffConcurrencyAsync();
    }

    private WechatRobotDbContext Database() => new(new DbContextOptionsBuilder<WechatRobotDbContext>()
        .UseMySQL(_fixture.ConnectionString).Options);

    private async Task VerifyRelationalHandoffConcurrencyAsync()
    {
        var actor = new ApplicationUser { Id = Guid.NewGuid(), UserName = "actor-" + Guid.NewGuid().ToString("N"), NormalizedUserName = Guid.NewGuid().ToString("N") };
        var firstAgent = new ApplicationUser { Id = Guid.NewGuid(), UserName = "agent1-" + Guid.NewGuid().ToString("N"), NormalizedUserName = Guid.NewGuid().ToString("N") };
        var secondAgent = new ApplicationUser { Id = Guid.NewGuid(), UserName = "agent2-" + Guid.NewGuid().ToString("N"), NormalizedUserName = Guid.NewGuid().ToString("N") };
        var thirdAgent = new ApplicationUser { Id = Guid.NewGuid(), UserName = "agent3-" + Guid.NewGuid().ToString("N"), NormalizedUserName = Guid.NewGuid().ToString("N") };
        var robot = new RobotConfigEntity { Name = "race-" + Guid.NewGuid().ToString("N"), WorkToolRobotId = Guid.NewGuid().ToString("N"), CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = Guid.NewGuid().ToString("N"), Name = "并发群" };
        var message = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name, SenderDisplayName = "客户",
            Text = "转人工", FallbackHash = Guid.NewGuid().ToString("N") };
        await using (var setup = Database()) { setup.AddRange(actor, firstAgent, secondAgent, thirdAgent, robot, group, message); await setup.SaveChangesAsync(TestContext.Current.CancellationToken); }
        var command = new StartHandoffCommand(message.Id, robot.Id, group.Id, robot.WorkToolRobotId, group.Name, "explicit_transfer", "{}",
            HandoffPauseScope.Group, null, firstAgent.Id, firstAgent.UserName!, "relational-handoff");
        await using var left = Database(); await using var right = Database();
        var starts = await Task.WhenAll(new EfHandoffStore(left).StartAsync(command, DateTime.UtcNow, TestContext.Current.CancellationToken),
            new EfHandoffStore(right).StartAsync(command, DateTime.UtcNow, TestContext.Current.CancellationToken));
        Assert.Equal(starts[0].Id, starts[1].Id);
        await using (var verify = Database())
        {
            Assert.Equal(1, await verify.SendCommands.CountAsync(x => x.IdempotencyKey == "relational-handoff", TestContext.Current.CancellationToken));
            Assert.Equal(2, await verify.HandoffTransitions.CountAsync(x => x.HandoffCaseId == starts[0].Id, TestContext.Current.CancellationToken));
        }
        await using var assignmentLeft = Database(); await using var assignmentRight = Database();
        async Task<Exception?> Assign(EfHandoffStore store, Guid target)
        { try { await store.AssignAsync(starts[0].Id, actor.Id, target, starts[0].Version, DateTime.UtcNow, TestContext.Current.CancellationToken); return null; } catch (Exception ex) { return ex; } }
        var assignments = await Task.WhenAll(Assign(new(assignmentLeft), secondAgent.Id), Assign(new(assignmentRight), thirdAgent.Id));
        Assert.Single(assignments, x => x is null);
        Assert.Single(assignments, x => x is HandoffConcurrencyException);
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(ModelProviderConfiguration configuration, EmbeddingBatchRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new EmbeddingBatchResponse(request.Inputs.Select(_ =>
                (IReadOnlyList<float>)[0.9f, 0.1f, 0.2f]).ToArray()));
    }

    private sealed class EvidenceAnswerChat : IChatCompletionClient
    {
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatCompletionResponse("请提交订单号。"));
    }
}
