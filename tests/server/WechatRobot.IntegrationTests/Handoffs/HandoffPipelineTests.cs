using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Handoffs;

public sealed class HandoffPipelineTests
{
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
        Assert.False(await service.IsPausedAsync(group.Id, null, TestContext.Current.CancellationToken));
    }

    private static WechatRobotDbContext Database()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new WechatRobotDbContext(options);
    }
}
