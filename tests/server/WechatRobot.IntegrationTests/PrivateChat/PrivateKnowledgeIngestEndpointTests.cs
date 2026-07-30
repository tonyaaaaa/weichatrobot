using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Identity;

namespace WechatRobot.IntegrationTests.PrivateChat;

public sealed class PrivateKnowledgeIngestEndpointTests
    : IClassFixture<UserAdministrationApiFactory>
{
    private readonly UserAdministrationApiFactory factory;

    public PrivateKnowledgeIngestEndpointTests(UserAdministrationApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Admin_can_list_failed_batch_and_retry_it_without_exposing_source_text()
    {
        await factory.ResetAsync();
        var admin = await factory.CreateUserAsync(
            "private-ingest-admin@example.test",
            "Private Ingest Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        var batchId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var robotId = Guid.NewGuid();
            db.RobotConfigs.Add(new RobotConfigEntity
            {
                Id = robotId,
                Name = "测试机器人",
                EncryptedWorkToolRobotId = "encrypted"
            });
            db.ConversationMessages.Add(new ConversationMessageEntity
            {
                Id = messageId,
                RobotConfigId = robotId,
                WorkToolMessageId = "private-ingest-message-1",
                FallbackHash = "private-ingest-message-1",
                FallbackWindowStartUtc = DateTime.UnixEpoch,
                ChannelType = "Private",
                RoomType = 4,
                PeerDisplayName = "内部同事",
                ScopeHash = "scope",
                SenderDisplayName = "内部同事",
                Text = "#知识入库\n这段原始内容不应出现在列表响应",
                ReceivedAtUtc = DateTime.UtcNow
            });
            db.PrivateKnowledgeIngestBatches.Add(new PrivateKnowledgeIngestBatchEntity
            {
                Id = batchId,
                RobotConfigId = robotId,
                SourceConversationMessageId = messageId,
                RoomType = 4,
                SourceActorDisplayName = "内部同事",
                Status = "Failed",
                FailureCode = "agent_invalid_output",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.DurableJobs.Add(new DurableJobEntity
            {
                Id = batchId,
                JobType = "ProcessPrivateKnowledgeIngest",
                RelatedConversationMessageId = messageId,
                PayloadJson = """{"batchId":"00000000-0000-0000-0000-000000000000","sourceText":"secret"}""",
                Status = "completed"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient(admin);
        var listResponse = await client.GetAsync(
            "/api/admin/private-knowledge-ingests?status=Failed",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var body = await listResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(batchId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("这段原始内容", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceText", body, StringComparison.OrdinalIgnoreCase);

        var retry = await client.PostAsJsonAsync(
            $"/api/admin/private-knowledge-ingests/{batchId:D}/retry",
            new { expectedVersion = 0 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var batch = await verification.PrivateKnowledgeIngestBatches
            .AsNoTracking()
            .SingleAsync(x => x.Id == batchId, TestContext.Current.CancellationToken);
        var job = await verification.DurableJobs
            .AsNoTracking()
            .SingleAsync(x => x.Id == batchId, TestContext.Current.CancellationToken);
        Assert.Equal("Received", batch.Status);
        Assert.Equal("pending", job.Status);
        Assert.DoesNotContain("secret", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }
}
