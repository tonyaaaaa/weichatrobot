using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.IntegrationTests.Handoffs;

public sealed class HandoffPipelineTests
{
    [Fact]
    public async Task Manual_handoff_on_disabled_robot_is_durable_and_queues_a_blocked_notification()
    {
        await using var db = Database();
        var robot = new RobotConfigEntity { Name = "disabled", WorkToolRobotId = "disabled-robot", CallbackSecretHash = "hash", IsEnabled = false };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = "disabled-group", Name = "禁用群" };
        var message = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name,
            SenderDisplayName = "客户", Text = "人工", FallbackHash = Guid.NewGuid().ToString("N") };
        db.AddRange(robot, group, message); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var actor = Guid.NewGuid();
        var started = await new HandoffService(new EfHandoffStore(db), TimeProvider.System)
            .StartManualAsync(new(message.Id, "人工", HandoffPauseScope.Group, null, "disabled", actor), TestContext.Current.CancellationToken);
        Assert.Equal("WaitingHuman", started.State);
        Assert.Equal("blocked", (await db.SendCommands.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(actor, (await db.HandoffTransitions.SingleAsync(TestContext.Current.CancellationToken)).ActorUserId);
    }

    [Fact]
    public async Task Manual_handoff_derives_robot_group_sender_scope_and_mention_from_server_data()
    {
        await using var db = Database();
        var agent = new ApplicationUser { Id = Guid.NewGuid(), UserName = "agent.zhang", NormalizedUserName = "AGENT.ZHANG" };
        var robot = new RobotConfigEntity { Name = "r", WorkToolRobotId = "server-robot", CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = "external-1", Name = "技术部" };
        var message = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = "caller-spoofed-name",
            SenderDisplayName = "客户", StableSenderId = "stable-customer", Text = "人工", FallbackHash = Guid.NewGuid().ToString("N") };
        db.AddRange(agent, robot, group, message); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new HandoffService(new EfHandoffStore(db), TimeProvider.System);

        var actor = Guid.NewGuid();
        var first = await service.StartManualAsync(new(message.Id, " 需要专员 ", HandoffPauseScope.Sender, agent.Id, "manual-1", actor), TestContext.Current.CancellationToken);
        var sameKey = await service.StartManualAsync(new(message.Id, "需要专员", HandoffPauseScope.Sender, agent.Id, "manual-1", actor), TestContext.Current.CancellationToken);
        var sameQuestion = await service.StartManualAsync(new(message.Id, "需要专员", HandoffPauseScope.Sender, agent.Id, "manual-2", actor), TestContext.Current.CancellationToken);
        Assert.Equal(first.Id, sameKey.Id);
        Assert.Equal(first.Id, sameQuestion.Id);
        await Assert.ThrowsAsync<HandoffStateException>(() => service.StartManualAsync(
            new(message.Id, "需要专员", HandoffPauseScope.Sender, agent.Id, "manual-1", Guid.NewGuid()), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HandoffStateException>(() => service.StartManualAsync(
            new(message.Id, "不同原因", HandoffPauseScope.Sender, agent.Id, "manual-1", Guid.NewGuid()), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HandoffStateException>(() => service.StartManualAsync(
            new(message.Id, "需要专员", HandoffPauseScope.Group, agent.Id, "manual-3", Guid.NewGuid()), TestContext.Current.CancellationToken));

        var handoff = await db.HandoffCases.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("stable-customer", handoff.StableSenderId);
        using var payload = JsonDocument.Parse((await db.SendCommands.SingleAsync(TestContext.Current.CancellationToken)).PayloadJson);
        Assert.False(payload.RootElement.TryGetProperty("WorkToolRobotId", out _));
        Assert.DoesNotContain("server-robot", payload.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("技术部", payload.RootElement.GetProperty("GroupName").GetString());
        Assert.Equal("agent.zhang", payload.RootElement.GetProperty("AtList")[0].GetString());
        Assert.Equal("manual:manual-1", handoff.StartIdempotencyKey);
        Assert.Equal(64, handoff.RequestFingerprint!.Length);
        Assert.All(await db.HandoffTransitions.ToArrayAsync(TestContext.Current.CancellationToken), transition => Assert.Equal(actor, transition.ActorUserId));

        var other = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name,
            SenderDisplayName = "另一客户", StableSenderId = "stable-other", Text = "人工", FallbackHash = Guid.NewGuid().ToString("N") };
        db.ConversationMessages.Add(other); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HandoffStateException>(() => service.StartManualAsync(
            new(other.Id, "需要专员", HandoffPauseScope.Sender, agent.Id, "manual-1", Guid.NewGuid()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Handoff_is_durable_idempotent_and_only_authenticated_resolution_creates_candidate()
    {
        await using var db = Database();
        var robot = new RobotConfigEntity { Name = "r", WorkToolRobotId = "robot-1", CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = "技术部", Name = "技术部" };
        var question = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, GroupName = group.Name,
            SenderDisplayName = "客户", StableSenderId = "customer-1", Text = "转人工", FallbackHash = Guid.NewGuid().ToString("N") };
        db.AddRange(robot, group, question);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new HandoffService(new EfHandoffStore(db), TimeProvider.System);
        var key = "handoff:" + question.Id;

        var first = await service.StartAsync(new StartHandoffCommand(question.Id, robot.Id, group.Id, robot.WorkToolRobotId, group.Name,
            "explicit_transfer", "{\"trigger\":\"phrase\"}", HandoffPauseScope.Group, null, Guid.NewGuid(), "张工", key), TestContext.Current.CancellationToken);
        var duplicate = await service.StartAsync(new StartHandoffCommand(question.Id, robot.Id, group.Id, robot.WorkToolRobotId, group.Name,
            "explicit_transfer", "{\"trigger\":\"phrase\"}", HandoffPauseScope.Group, null, first.AssigneeUserId, "张工", key), TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(await db.HandoffCases.ToArrayAsync(TestContext.Current.CancellationToken));
        var initialTransitions = await db.HandoffTransitions.OrderBy(x => x.Sequence).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, initialTransitions.Length);
        Assert.Equal(("AIActive", "WaitingHuman"), (initialTransitions[0].FromState, initialTransitions[0].ToState));
        Assert.Equal(("WaitingHuman", "HumanHandling"), (initialTransitions[1].FromState, initialTransitions[1].ToState));
        Assert.All(initialTransitions, transition => Assert.Null(transition.ActorUserId));
        var send = Assert.Single(await db.SendCommands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(key, send.IdempotencyKey);
        using var payload = JsonDocument.Parse(send.PayloadJson);
        var notification = payload.RootElement.GetProperty("Text").GetString();
        Assert.Equal("张工", payload.RootElement.GetProperty("AtList")[0].GetString());
        Assert.Contains("explicit_transfer", notification);
        Assert.True(await service.IsPausedAsync(group.Id, null, TestContext.Current.CancellationToken));

        var reassignedUser = Guid.NewGuid();
        var reassigned = await service.AssignAsync(first.Id, Guid.NewGuid(), reassignedUser, first.Version, TestContext.Current.CancellationToken);
        var duplicateAssignment = await service.AssignAsync(first.Id, Guid.NewGuid(), reassignedUser, first.Version, TestContext.Current.CancellationToken);
        Assert.Equal(reassigned.Version, duplicateAssignment.Version);

        await service.RecordUnverifiedWorkToolMessageAsync(first.Id, "external-1", "张工", "这是答案", TestContext.Current.CancellationToken);
        Assert.Empty(await db.KnowledgeCandidates.ToArrayAsync(TestContext.Current.CancellationToken));

        var candidate = await service.ResolveAsync(first.Id, reassignedUser, "这是经人工确认的答案", reassigned.Version,
            TestContext.Current.CancellationToken);
        Assert.Equal("pending", candidate.Status);
        Assert.Equal("这是经人工确认的答案", candidate.Answer);
        Assert.Single(await db.HandoffMessages.Where(x => x.AuthenticationKind == "worktool_display_name_unverified").ToArrayAsync(TestContext.Current.CancellationToken));
        var resolved = await db.HandoffCases.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var restored = await service.RestoreAiAsync(first.Id, reassignedUser, resolved.Version, TestContext.Current.CancellationToken);
        var duplicateRestore = await service.RestoreAiAsync(first.Id, reassignedUser, resolved.Version, TestContext.Current.CancellationToken);
        Assert.Equal(restored.Version, duplicateRestore.Version);
        Assert.Contains(await db.HandoffTransitions.ToArrayAsync(TestContext.Current.CancellationToken), x => x.FromState == "Resolved" && x.ToState == "AIActive");
        Assert.False(await service.IsPausedAsync(group.Id, null, TestContext.Current.CancellationToken));
    }

    private static WechatRobotDbContext Database()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new WechatRobotDbContext(options);
    }
}
