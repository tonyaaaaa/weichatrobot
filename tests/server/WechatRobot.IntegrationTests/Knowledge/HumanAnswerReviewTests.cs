using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Handoffs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class HumanAnswerReviewTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;
    public HumanAnswerReviewTests(MySqlFixture fixture) => _fixture = fixture;
    [Fact]
    public async Task Approved_answer_is_only_published_after_its_index_generation_becomes_active()
    {
        await using var db = Database();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var tag = new KnowledgeTagEntity { Name = "售后", NormalizedName = "售后", IsEnabled = true };
        var robot = new RobotConfigEntity { Name = "review-" + Guid.NewGuid().ToString("N"), WorkToolRobotId = Guid.NewGuid().ToString("N"), CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = Guid.NewGuid().ToString("N"), Name = "技术部" };
        var question = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name, SenderDisplayName = "客户",
            Text = "如何申请售后？", FallbackHash = Guid.NewGuid().ToString("N") };
        var handoff = new HandoffCaseEntity { QuestionMessageId = question.Id, RobotConfigId = robot.Id, GroupProfileId = group.Id,
            State = "Resolved", ReasonCode = "manual", EvidenceJson = "[]", PauseScope = "Group", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        var candidate = new KnowledgeCandidateEntity { HandoffCaseId = handoff.Id, QuestionMessageId = question.Id,
            Question = "如何申请售后？", Answer = "请提交订单号。", EvidenceJson = "[]", Status = "pending", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.AddRange(tag, robot, group, question, handoff, candidate);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var options = new KnowledgeIndexOptions(3, VectorDistance.Cosine);
        var knowledge = new QdrantKnowledgeService(db, new ModelConfigurationService(new FakeProtector()), options, TimeProvider.System);
        var service = new KnowledgeCandidateService(new EfKnowledgeCandidateStore(db, knowledge), TimeProvider.System);
        var reviewer = Guid.NewGuid();

        var approved = await service.ReviewAsync(new(candidate.Id, reviewer, "approve", [tag.Id], null, "review-1", 0), TestContext.Current.CancellationToken);
        var duplicate = await service.ReviewAsync(new(candidate.Id, reviewer, "approve", [tag.Id], null, "review-1", 0), TestContext.Current.CancellationToken);

        Assert.Equal(approved.IndexJobId, duplicate.IndexJobId);
        Assert.Equal("indexing", approved.Status);
        Assert.False((await db.KnowledgeCandidates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).PublishedAtUtc.HasValue);
        var chunk = await db.KnowledgeChunks.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("如何申请售后？", chunk.Question);
        Assert.Equal("请提交订单号。", chunk.Answer);
        Assert.Equal("approved", chunk.Status);

        var leased = await knowledge.LeaseNextAsync("test", DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.NotNull(leased);
        var work = await knowledge.LoadIndexWorkAsync(leased!.Id, TestContext.Current.CancellationToken);
        Assert.True(await knowledge.ActivateVersionAsync(work, TestContext.Current.CancellationToken));

        var published = await db.KnowledgeCandidates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("published", published.Status);
        Assert.NotNull(published.PublishedAtUtc);
    }

    private WechatRobotDbContext Database() => new(new DbContextOptionsBuilder<WechatRobotDbContext>()
        .UseMySQL(_fixture.ConnectionString).Options);

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
