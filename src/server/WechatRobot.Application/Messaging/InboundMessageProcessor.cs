using System.Text.Json;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Models;
using WechatRobot.Application.Handoffs;

namespace WechatRobot.Application.Messaging;

public sealed class InboundMessageProcessor(
    IGroundedConversationRepository conversations,
    ConversationContextService context,
    RetrievalQueryBuilder retrievalQueries,
    IConversationSummarizer summarizer,
    GroundedAnswerService answers,
    TimeProvider timeProvider,
    IHandoffOrchestrator? handoffs = null)
{
    private static readonly TimeSpan SessionLeaseDuration = TimeSpan.FromMinutes(1);
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

        var sessionLeaseOwner = $"{job.LeaseOwner}:{job.Id:N}";
        var request = await conversations.LeaseForProcessingAsync(payload.MessageId, sessionLeaseOwner, timeProvider.GetUtcNow().UtcDateTime,
            SessionLeaseDuration, cancellationToken);
        var committed = false;
        try
        {
        if (handoffs is not null && await handoffs.IsPausedAsync(request, cancellationToken))
        {
            await conversations.PersistHandoffTerminalAsync(request, HandoffResult("already_paused"), cancellationToken);
            committed = true;
            return;
        }
        var effectiveContext = context.Build(request.History, request.ContextPolicy, request.Scope.ScopeKey, request.ReceivedAtUtc, request.Summary);
        string? updatedSummary = null;
        string? summaryFailureCode = null;
        if (request.ContextPolicy.SummaryEnabled && effectiveContext.EvictedMessages.Count > 0)
        {
            await EnsureLeaseAsync(request, cancellationToken);
            try
            {
                updatedSummary = await summarizer.SummarizeAsync(request.ChatConfiguration, request.Summary, effectiveContext.EvictedMessages, cancellationToken);
                effectiveContext = context.Build(effectiveContext.Messages, request.ContextPolicy, request.Scope.ScopeKey, request.ReceivedAtUtc, updatedSummary);
            }
            catch (ModelUnavailableException)
            {
                summaryFailureCode = "summary_provider_unavailable";
                effectiveContext = new(effectiveContext.Messages, null, effectiveContext.WasIdleReset, effectiveContext.WasTokenLimited,
                    effectiveContext.EvictedMessages, effectiveContext.ContextTokenCount);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                summaryFailureCode = "summary_provider_timeout";
                effectiveContext = new(effectiveContext.Messages, null, effectiveContext.WasIdleReset, effectiveContext.WasTokenLimited,
                    effectiveContext.EvictedMessages, effectiveContext.ContextTokenCount);
            }
        }
        var retrievalQuery = retrievalQueries.Build(request.Question, effectiveContext);
        await EnsureLeaseAsync(request, cancellationToken);
        var result = await answers.AnswerAsync(new(request.MessageId, request.GroupProfileId, request.Scope.ScopeKey, request.Question,
            request.AllowedTagIds, effectiveContext, request.ContextPolicy, request.ChatConfiguration, retrievalQuery, request.ModelConfigurationId,
            request.Scope.DegradationReason, summaryFailureCode), cancellationToken);
        if (effectiveContext.WasIdleReset) result = result with { ResetContextBeforeCurrent = true, UpdatedSummary = null };
        if (updatedSummary is not null) result = result with { UpdatedSummary = updatedSummary };
        if (handoffs is not null && await handoffs.HandleDecisionAsync(request, result, cancellationToken))
        {
            await EnsureLeaseAsync(request, cancellationToken);
            await conversations.PersistHandoffTerminalAsync(request, HandoffResult(result.Audit.FailureCode ?? "handoff_triggered", result.Audit), cancellationToken);
            committed = true;
            return;
        }
        await EnsureLeaseAsync(request, cancellationToken);
        if (handoffs is not null && await handoffs.IsPausedAsync(request, cancellationToken))
        {
            await conversations.PersistHandoffTerminalAsync(request, HandoffResult("manual_handoff_race", result.Audit), cancellationToken);
            committed = true;
            return;
        }
        try
        {
            await conversations.PersistAnswerAndEnqueueAsync(request, result, cancellationToken);
        }
        catch (ConversationHandoffRaceException)
        {
            await conversations.PersistHandoffTerminalAsync(request, HandoffResult("manual_handoff_commit_race", result.Audit), cancellationToken);
        }
        committed = true;
        }
        finally
        {
            if (!committed) await conversations.ReleaseLeaseAsync(request.ConversationSessionId, sessionLeaseOwner, CancellationToken.None);
        }
    }

    private static GroundedAnswerResult HandoffResult(string reason, RetrievalAuditDraft? source = null) => new(
        new(AnswerDecisionKind.Handoff, string.Empty), source is null
            ? new([], 0, null, "handoff", AnswerDecisionKind.Handoff.ToString(), reason)
            : source with { Decision = AnswerDecisionKind.Handoff.ToString(), FailureCode = reason });

    private async Task EnsureLeaseAsync(ConversationProcessingRequest request, CancellationToken token)
    {
        if (!await conversations.RenewLeaseAsync(request.ConversationSessionId, request.SessionLeaseOwner!, timeProvider.GetUtcNow().UtcDateTime,
                SessionLeaseDuration, token))
            throw new ConversationSessionOwnershipLostException("Conversation session lease renewal failed.");
    }

    private sealed class InboundMessagePayload
    {
        public Guid MessageId { get; init; }
        public Guid RobotConfigId { get; init; }
        public string WorkToolRobotId { get; init; } = string.Empty;
        public string GroupName { get; init; } = string.Empty;
    }
}
