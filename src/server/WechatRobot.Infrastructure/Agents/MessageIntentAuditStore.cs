using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Agents;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Agents;

public sealed class MessageIntentAuditStore(WechatRobotDbContext database)
    : IMessageIntentAuditStore
{
    public async Task RecordAsync(
        MessageIntentAuditRecord record,
        CancellationToken cancellationToken)
    {
        if (await database.Set<MessageIntentAuditEntity>().AnyAsync(
                item => item.ConversationMessageId == record.MessageId,
                cancellationToken))
        {
            return;
        }
        database.Add(new MessageIntentAuditEntity
        {
            ConversationMessageId = record.MessageId,
            GroupProfileId = record.GroupProfileId,
            IntentDecision = record.Result.Decision.ToString(),
            IntentCategory = record.Result.Category.ToString(),
            IntentReasonCode = record.Result.ReasonCode,
            IntentConfidence = record.Result.Confidence,
            FailureCode = record.Result.FailureCode,
            IntentRuntimeMode = record.RuntimeMode.ToString(),
            IntentAgentVersion = record.Result.AgentVersion,
            IntentModelConfigurationId = record.Result.ModelConfigurationId,
            IntentModelVersion = record.Result.ModelConfigurationVersion,
            IntentLatencyMilliseconds = record.Result.LatencyMilliseconds,
            FormalConversationIncluded = record.FormalConversationIncluded,
            IntentDecidedAtUtc = record.DecidedAtUtc,
            CreatedAtUtc = record.DecidedAtUtc
        });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is MySqlException { Number: 1062 })
        {
            database.ChangeTracker.Clear();
        }
    }
}
