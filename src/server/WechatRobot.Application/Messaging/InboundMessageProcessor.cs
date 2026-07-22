using System.Text.Json;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Conversations;

namespace WechatRobot.Application.Messaging;

public sealed class InboundMessageProcessor(
    IGroundedConversationRepository conversations,
    ConversationContextService context,
    GroundedAnswerService answers)
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

        var request = await conversations.LoadForProcessingAsync(payload.MessageId, cancellationToken);
        var effectiveContext = context.Build(request.History, request.ContextPolicy, request.SenderExternalUserId, request.ReceivedAtUtc, request.Summary);
        var result = await answers.AnswerAsync(new(request.MessageId, request.GroupProfileId, request.SenderExternalUserId, request.Question,
            request.AllowedTagIds, effectiveContext, request.ContextPolicy, request.ChatConfiguration), cancellationToken);
        await conversations.PersistAnswerAndEnqueueAsync(request, result, cancellationToken);
    }

    private sealed class InboundMessagePayload
    {
        public Guid MessageId { get; init; }
        public Guid RobotConfigId { get; init; }
        public string WorkToolRobotId { get; init; } = string.Empty;
        public string GroupName { get; init; } = string.Empty;
    }
}
