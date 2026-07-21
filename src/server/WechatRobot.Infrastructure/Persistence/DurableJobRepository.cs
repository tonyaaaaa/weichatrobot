using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Jobs;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class DurableJobRepository(WechatRobotDbContext database) : IDurableJobRepository
{
    public async Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var message = new ConversationMessageEntity
        {
            RobotConfigId = request.RobotConfigId,
            WorkToolMessageId = string.IsNullOrWhiteSpace(request.WorkToolMessageId) ? null : request.WorkToolMessageId,
            FallbackHash = request.FallbackHash,
            FallbackWindowStartUtc = request.FallbackWindowStartUtc,
            SenderExternalUserId = request.SenderName,
            Text = request.Text,
            ReceivedAtUtc = request.ReceivedAtUtc
        };
        database.ConversationMessages.Add(message);
        database.DurableJobs.Add(new DurableJobEntity
        {
            JobType = "ProcessInboundMessage",
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = message.Id,
                request.RobotConfigId,
                request.GroupName,
                request.SenderName,
                request.Text,
                request.ReceivedAtUtc
            })
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InboundMessageIngestResult.Accepted;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return InboundMessageIngestResult.Duplicate;
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) => exception.InnerException is MySqlException { Number: 1062 };
}
