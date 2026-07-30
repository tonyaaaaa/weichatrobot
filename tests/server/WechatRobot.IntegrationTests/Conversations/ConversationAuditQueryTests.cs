using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WechatRobot.Application.Audit;
using WechatRobot.Infrastructure.Conversations;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Conversations;

public sealed class ConversationAuditQueryTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Query_uses_bounded_batches_and_exact_send_keys_under_same_group_traffic()
    {
        var counter = new CommandCounter();
        await using var db = Database(counter);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var at = new DateTime(2026, 7, 23, 2, 0, 0, DateTimeKind.Utc);
        var robot = new RobotConfigEntity { Name = "audit-query", WorkToolRobotId = $"audit-{Guid.NewGuid():N}", CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = $"group-{Guid.NewGuid():N}", Name = "技术部" };
        var first = Message(robot.Id, group.Id, "first", at);
        var second = Message(robot.Id, group.Id, "second", at.AddMilliseconds(1));
        var firstAudit = Audit(first.Id, group.Id, at.AddSeconds(1));
        var secondAudit = Audit(second.Id, group.Id, at.AddSeconds(2));
        var handoffKey = $"handoff-second-{Guid.NewGuid():N}";
        var handoff = new HandoffCaseEntity
        {
            QuestionMessageId = second.Id, RobotConfigId = robot.Id, GroupProfileId = group.Id,
            State = "WaitingHuman", ReasonCode = "explicit_transfer", EvidenceJson = "{}",
            StartIdempotencyKey = handoffKey, CreatedAtUtc = at.AddSeconds(2), UpdatedAtUtc = at.AddSeconds(2)
        };
        var transition = new HandoffTransitionEntity
        {
            HandoffCaseId = handoff.Id, Sequence = 1, FromState = "AIActive", ToState = "WaitingHuman",
            ReasonCode = "explicit_transfer", IdempotencyKey = $"transition-{Guid.NewGuid():N}", CreatedAtUtc = at.AddSeconds(2)
        };
        var candidate = new KnowledgeCandidateEntity
        {
            HandoffCaseId = handoff.Id, QuestionMessageId = second.Id, Question = second.Text, Answer = "human answer",
            EvidenceJson = "{}", CreatedAtUtc = at.AddSeconds(3), UpdatedAtUtc = at.AddSeconds(3)
        };
        db.AddRange(robot, group, first, second, firstAudit, secondAudit,
            Send(robot.Id, group.Id, "unrelated-concurrent-send", "decoy", "dead_letter", at.AddSeconds(1)),
            Send(robot.Id, group.Id, $"grounded-reply:{first.Id:D}", "first answer", "completed", at.AddSeconds(10)),
            Send(robot.Id, group.Id, handoffKey, "second handoff", "retrying", at),
            handoff, transition, candidate);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        counter.Reset();

        var query = new ConversationAuditQuery(db);
        var firstPage = await query.ListAsync(new(group.Id, null, null, null, 1, 1), TestContext.Current.CancellationToken);
        var oneItemQueries = counter.Count;
        counter.Reset();
        var fullPage = await query.ListAsync(new(group.Id, null, null, null, 1, 20), TestContext.Current.CancellationToken);

        Assert.Equal(5, oneItemQueries);
        Assert.Equal(oneItemQueries, counter.Count);
        Assert.Equal(2, fullPage.Total);
        Assert.Null(fullPage.Items.Single(item => item.MessageId == second.Id).Send);
        Assert.NotNull(fullPage.Items.Single(item => item.MessageId == second.Id).KnowledgeCandidate);
        Assert.Equal("completed", fullPage.Items.Single(item => item.MessageId == first.Id).Send?.Status);
        Assert.DoesNotContain(fullPage.Items, item => item.Send?.Status == "dead_letter");
    }

    [Fact]
    public async Task Retrieval_audit_persists_a_structured_model_configuration_reference()
    {
        var counter = new CommandCounter();
        await using var db = Database(counter);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var model = new ModelConfigEntity
        {
            Name = $"audit-model-{Guid.NewGuid():N}",
            NormalizedName = $"AUDIT-MODEL-{Guid.NewGuid():N}",
            Provider = "openai-compatible",
            ConfigurationType = "chat",
            BaseUrl = "https://provider.example.test",
            Model = "model"
        };
        var robot = new RobotConfigEntity
        {
            Name = "model-reference",
            WorkToolRobotId = $"model-reference-{Guid.NewGuid():N}",
            CallbackSecretHash = "hash"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = $"group-{Guid.NewGuid():N}",
            Name = "技术部"
        };
        var message = Message(robot.Id, group.Id, "model-reference", DateTime.UtcNow);
        var audit = Audit(message.Id, group.Id, DateTime.UtcNow);
        audit.ModelConfigurationId = model.Id;
        db.AddRange(model, robot, group, message, audit);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        Assert.Equal(
            model.Id,
            (await db.RetrievalAudits.AsNoTracking().SingleAsync(
                item => item.Id == audit.Id,
                TestContext.Current.CancellationToken)).ModelConfigurationId);
    }

    private WechatRobotDbContext Database(CommandCounter counter) => new(
        new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(fixture.ConnectionString).AddInterceptors(counter).Options);

    private static ConversationMessageEntity Message(Guid robotId, Guid groupId, string suffix, DateTime at) => new()
    {
        RobotConfigId = robotId, GroupProfileId = groupId, Direction = "inbound", Role = "user",
        WorkToolMessageId = $"audit-query-{suffix}-{Guid.NewGuid():N}", FallbackHash = $"fallback-{suffix}-{Guid.NewGuid():N}",
        FallbackWindowStartUtc = at, GroupName = "技术部", SenderDisplayName = suffix, Text = $"{suffix} question",
        ProcessingState = "completed", ReceivedAtUtc = at, CreatedAtUtc = at
    };

    private static RetrievalAuditEntity Audit(Guid messageId, Guid groupId, DateTime at) => new()
    {
        ConversationMessageId = messageId, GroupProfileId = groupId, Decision = "Answer", ConfidenceThreshold = .7,
        ConfidenceValue = .9, ContextPolicy = "group", EvidenceJson = "[]", InputSummaryJson = "{}", CreatedAtUtc = at
    };

    private static SendCommandEntity Send(Guid robotId, Guid groupId, string key, string text, string status, DateTime at) => new()
    {
        RobotConfigId = robotId, GroupProfileId = groupId, IdempotencyKey = key,
        PayloadJson = $$"""{"Text":"{{text}}"}""", Status = status, CreatedAtUtc = at, NextAttemptAtUtc = at
    };

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public void Reset() => Count = 0;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
