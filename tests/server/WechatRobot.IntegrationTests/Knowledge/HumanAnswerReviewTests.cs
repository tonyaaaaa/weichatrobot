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
        await Assert.ThrowsAsync<HandoffStateException>(() => service.ReviewAsync(
            new(candidate.Id, reviewer, "approve", [tag.Id], "不同答案", "review-1", 0), TestContext.Current.CancellationToken));

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
        await new KnowledgeCandidatePublishProcessor(db, knowledge, TimeProvider.System).ProcessAsync(leasedPublish!, TestContext.Current.CancellationToken);
        Assert.Equal("indexing", (await db.KnowledgeCandidates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Status);

        var leased = await knowledge.LeaseNextAsync("test", DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.NotNull(leased);
        var vectors = new QdrantVectorStore(_qdrantHttp);
        var embeddings = new FakeEmbeddingClient();
        await new KnowledgeIndexService(embeddings, vectors, knowledge, options).IndexAsync(leased!.Id, TestContext.Current.CancellationToken);

        var published = await db.KnowledgeCandidates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("published", published.Status);
        Assert.NotNull(published.PublishedAtUtc);
        Assert.Equal("leased", (await db.DurableJobs.AsNoTracking().SingleAsync(x => x.Id == publishJob.Id, TestContext.Current.CancellationToken)).Status);
        await durable.CompleteJobAsync(publishJob.Id, "publisher", DateTime.UtcNow, TestContext.Current.CancellationToken);
        var retrieval = new KnowledgeRetrievalEvidenceProvider(db, knowledge, embeddings, vectors);
        var evidence = await retrieval.RetrieveAsync("售后申请需要提供什么？", [tag.Id], 3, TestContext.Current.CancellationToken);
        Assert.Contains("请提交订单号。", Assert.Single(evidence).Text);
        var grounded = await new GroundedAnswerService(retrieval, new EvidenceAnswerChat(), new GroundedAnswerOptions(.1), new AnswerOutputFirewall())
            .AnswerAsync(new(Guid.NewGuid(), group.Id, "group", "售后申请需要提供什么？", [tag.Id], new([], null, false, false, [], 0),
                new(false, 3, 30, 3000, false, false), new("https://fake.test", "fake", "fake", TimeSpan.FromSeconds(1), 0)), TestContext.Current.CancellationToken);
        Assert.Equal(AnswerDecisionKind.Answer, grounded.Decision.Kind);
        Assert.Equal("请提交订单号。", grounded.Decision.GroupText);
        var unrelated = await new GroundedAnswerService(retrieval, new EvidenceAnswerChat(), new GroundedAnswerOptions(.1), new AnswerOutputFirewall())
            .AnswerAsync(new(Guid.NewGuid(), group.Id, "group", "今天天气如何？", [tag.Id], new([], null, false, false, [], 0),
                new(false, 3, 30, 3000, false, false), new("https://fake.test", "fake", "fake", TimeSpan.FromSeconds(1), 0)), TestContext.Current.CancellationToken);
        Assert.Equal(AnswerDecisionKind.InsufficientEvidence, unrelated.Decision.Kind);
        await VerifyApprovalValidationAndConcurrencyAsync(robot, group, reviewer, reviewerUser.UserName!, tag.Id);
        await vectors.DeleteCollectionAsync(new(published.KnowledgeDocumentVersionId is null ? "unused" :
            (await db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(x => x.Id == published.KnowledgeDocumentVersionId, TestContext.Current.CancellationToken)).IndexCollectionName!, 3, VectorDistance.Cosine),
            TestContext.Current.CancellationToken);

        await VerifyRelationalHandoffConcurrencyAsync();
        await VerifyHandoffWinsFinalAnswerCommitRaceAsync();
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

    private async Task VerifyApprovalValidationAndConcurrencyAsync(RobotConfigEntity robot, GroupProfileEntity group, Guid reviewer,
        string reviewerName, Guid enabledTagId)
    {
        var question = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name,
            SenderDisplayName = "客户", Text = "并发审核问题", FallbackHash = Guid.NewGuid().ToString("N") };
        Guid candidateId;
        await using (var setup = Database())
        {
            setup.ConversationMessages.Add(question); await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            var handoff = new HandoffService(new EfHandoffStore(setup), TimeProvider.System);
            var started = await handoff.StartAsync(new(question.Id, robot.Id, group.Id, robot.WorkToolRobotId, group.Name, "manual_transfer", "{}",
                HandoffPauseScope.Group, null, reviewer, reviewerName, "approval-race-handoff"), TestContext.Current.CancellationToken);
            candidateId = (await handoff.ResolveAsync(started.Id, reviewer, "并发审核答案", started.Version, TestContext.Current.CancellationToken)).Id;
        }
        await using (var invalid = Database())
        {
            var service = new KnowledgeCandidateService(new EfKnowledgeCandidateStore(invalid), TimeProvider.System);
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewAsync(new(candidateId, reviewer, "approve", [Guid.NewGuid()], null,
                "invalid-tag", 0), TestContext.Current.CancellationToken));
            Assert.Equal(0, await invalid.KnowledgeReviews.CountAsync(x => x.KnowledgeCandidateId == candidateId, TestContext.Current.CancellationToken));
        }
        async Task<Exception?> Approve(string key)
        {
            await using var context = Database();
            try
            {
                await new KnowledgeCandidateService(new EfKnowledgeCandidateStore(context), TimeProvider.System)
                    .ReviewAsync(new(candidateId, reviewer, "approve", [enabledTagId], null, key, 0), TestContext.Current.CancellationToken);
                return null;
            }
            catch (Exception exception) { return exception; }
        }
        var outcomes = await Task.WhenAll(Approve("approval-race-left"), Approve("approval-race-right"));
        Assert.Single(outcomes, x => x is null);
        Assert.Single(outcomes, x => x is HandoffConcurrencyException or HandoffStateException);
        await using var verify = Database();
        Assert.Equal(1, await verify.KnowledgeReviews.CountAsync(x => x.KnowledgeCandidateId == candidateId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.DurableJobs.CountAsync(x => x.Id == candidateId, TestContext.Current.CancellationToken));
        var storedKey = await verify.KnowledgeReviews.Where(x => x.KnowledgeCandidateId == candidateId).Select(x => x.IdempotencyKey)
            .SingleAsync(TestContext.Current.CancellationToken);
        var originalKey = storedKey.EndsWith("approval-race-left", StringComparison.Ordinal) ? "approval-race-left" : "approval-race-right";
        await using var failure = Database();
        var durable = new DurableJobRepository(failure);
        var publish = Assert.IsType<WechatRobot.Application.Jobs.LeasedDurableJob>(await durable.LeaseNextJobAsync("PublishKnowledgeCandidate",
            "failure-publisher", DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        var knowledge = new QdrantKnowledgeService(failure, new ModelConfigurationService(new FakeProtector()), new(3, VectorDistance.Cosine), TimeProvider.System);
        await new KnowledgeCandidatePublishProcessor(failure, knowledge, TimeProvider.System).ProcessAsync(publish, TestContext.Current.CancellationToken);
        await durable.CompleteJobAsync(publish.Id, publish.LeaseOwner, DateTime.UtcNow, TestContext.Current.CancellationToken);
        var index = Assert.IsType<LeasedKnowledgeIndexJob>(await knowledge.LeaseNextAsync("failed-index", DateTime.UtcNow.AddMinutes(1),
            TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        await knowledge.MarkIndexFailedAsync(index.Id, index.LeaseOwner, "forced terminal failure", false, TestContext.Current.CancellationToken);
        await new KnowledgeCandidateService(new EfKnowledgeCandidateStore(failure), TimeProvider.System)
            .ReviewAsync(new(candidateId, reviewer, "approve", [enabledTagId], null, originalKey, 0), TestContext.Current.CancellationToken);
        Assert.Equal("approved_pending_index", (await failure.KnowledgeCandidates.AsNoTracking().SingleAsync(x => x.Id == candidateId,
            TestContext.Current.CancellationToken)).Status);
        Assert.Equal("retrying", (await failure.DurableJobs.AsNoTracking().SingleAsync(x => x.Id == candidateId,
            TestContext.Current.CancellationToken)).Status);
    }

    private async Task VerifyHandoffWinsFinalAnswerCommitRaceAsync()
    {
        var robot = new RobotConfigEntity { Name = "commit-race-" + Guid.NewGuid().ToString("N"), WorkToolRobotId = Guid.NewGuid().ToString("N"), CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = Guid.NewGuid().ToString("N"), Name = "提交竞态群" };
        var message = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name,
            SenderDisplayName = "客户", StableSenderId = "stable-race", Text = "需要人工", FallbackHash = Guid.NewGuid().ToString("N"), ProcessingState = "processing" };
        var session = new ConversationSessionEntity { GroupProfileId = group.Id, SenderScopeKey = "group", LeaseOwner = "answer-owner",
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(5), LastActivityAtUtc = DateTime.UtcNow };
        await using (var setup = Database()) { setup.AddRange(robot, group, message, session); await setup.SaveChangesAsync(TestContext.Current.CancellationToken); }

        var request = new ConversationProcessingRequest(message.Id, robot.Id, robot.WorkToolRobotId, group.Id, group.Name, message.SenderDisplayName,
            message.StableSenderId, new("group", false, null), message.Text, DateTime.UtcNow, [], [], null,
            new(false, 3, 30, 3000, false, false), new("https://fake.test", "fake", "fake", TimeSpan.FromSeconds(1), 0),
            Guid.Empty, session.Id, "answer-owner", 0);
        var result = new GroundedAnswerResult(new(AnswerDecisionKind.Answer, "不应发送的回答"), new([], .7, .9, "policy", "Answer", InputSummaryJson: "{}"));
        var command = new StartHandoffCommand(message.Id, robot.Id, group.Id, robot.WorkToolRobotId, group.Name, "manual_transfer", "{}",
            HandoffPauseScope.Group, null, null, "人工客服", "commit-race-handoff");

        await using var gate = Database();
        await using var gateTransaction = await gate.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        _ = await gate.GroupProfiles.FromSqlInterpolated($"SELECT * FROM group_profile WHERE Id = {group.Id} FOR UPDATE")
            .SingleAsync(TestContext.Current.CancellationToken);
        var handoffTask = Task.Run(async () => { await using var context = Database(); return await new EfHandoffStore(context)
            .StartAsync(command, DateTime.UtcNow, TestContext.Current.CancellationToken); });
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var answerTask = Task.Run(async () =>
        {
            await using var context = Database();
            try
            {
                await new GroundedConversationRepository(context, new ModelConfigurationService(new FakeProtector()), TimeProvider.System)
                    .PersistAnswerAndEnqueueAsync(request, result, TestContext.Current.CancellationToken);
                return (Exception?)null;
            }
            catch (Exception exception) { return exception; }
        });
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await gateTransaction.CommitAsync(TestContext.Current.CancellationToken);
        _ = await handoffTask;
        Assert.IsType<ConversationHandoffRaceException>(await answerTask);
        await using var verify = Database();
        Assert.Equal(1, await verify.SendCommands.CountAsync(x => x.GroupProfileId == group.Id, TestContext.Current.CancellationToken));
        Assert.Equal(0, await verify.RetrievalAudits.CountAsync(x => x.GroupProfileId == group.Id, TestContext.Current.CancellationToken));
        Assert.Equal(0, await verify.ConversationMessages.CountAsync(x => x.InReplyToMessageId == message.Id, TestContext.Current.CancellationToken));
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(ModelProviderConfiguration configuration, EmbeddingBatchRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new EmbeddingBatchResponse(request.Inputs.Select(input =>
                (IReadOnlyList<float>)(input.Contains("天气", StringComparison.Ordinal) ? [0f, 1f, 0f] : [1f, 0f, 0f])).ToArray()));
    }

    private sealed class EvidenceAnswerChat : IChatCompletionClient
    {
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatCompletionResponse("请提交订单号。"));
    }
}
