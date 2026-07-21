using System.Text.Json;
using WechatRobot.Application.Jobs;

namespace WechatRobot.Application.Messaging;

public sealed class InboundMessageProcessor(SendCommandService sendCommands, FixedReplyOptions options)
{
    public async Task ProcessAsync(LeasedDurableJob job, CancellationToken cancellationToken)
    {
        if (!string.Equals(job.JobType, "ProcessInboundMessage", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported durable job type '{job.JobType}'.");
        }

        var payload = JsonSerializer.Deserialize<InboundMessagePayload>(job.PayloadJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })
            ?? throw new InvalidOperationException("Inbound durable job payload is invalid.");
        if (payload.MessageId == Guid.Empty || payload.RobotConfigId == Guid.Empty || string.IsNullOrWhiteSpace(payload.GroupName))
        {
            throw new InvalidOperationException("Inbound durable job payload is incomplete.");
        }

        await sendCommands.EnqueueFixedReplyAsync(
            payload.RobotConfigId,
            payload.WorkToolRobotId,
            payload.GroupName,
            options.Text,
            payload.MessageId,
            cancellationToken);
    }

    private sealed class InboundMessagePayload
    {
        public Guid MessageId { get; init; }
        public Guid RobotConfigId { get; init; }
        public string WorkToolRobotId { get; init; } = string.Empty;
        public string GroupName { get; init; } = string.Empty;
    }
}
