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

        await service.StartManualAsync(new(message.Id, "需要专员", HandoffPauseScope.Sender, agent.Id, "manual-1", Guid.NewGuid()), TestContext.Current.CancellationToken);

        var handoff = await db.HandoffCases.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("stable-customer", handoff.StableSenderId);
        using var payload = JsonDocument.Parse((await db.SendCommands.SingleAsync(TestContext.Current.CancellationToken)).PayloadJson);
        Assert.Equal("server-robot", payload.RootElement.GetProperty("WorkToolRobotId").GetString());
        Assert.Equal("技术部", payload.RootElement.GetProperty("GroupName").GetString());
        Assert.Contains("@agent.zhang", payload.RootElement.GetProperty("Text").GetString());
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
        var send = Assert.Single(await db.SendCommands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(key, send.IdempotencyKey);
        using var payload = JsonDocument.Parse(send.PayloadJson);
        var notification = payload.RootElement.GetProperty("Text").GetString();
        Assert.Contains("@张工", notification);
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
