using System.Text.Json;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Models;
using WechatRobot.Application.FixedReplies;
using WechatRobot.Application.Agents;

namespace WechatRobot.Application.Messaging;

public sealed class InboundMessageProcessor(
    IGroundedConversationRepository conversations,
    ConversationContextService context,
    IConversationSummarizer summarizer,
    GroundedAnswerService answers,
    TimeProvider timeProvider,
    MultiTurnRetrievalService multiTurnRetrieval,
    ITemplateRoutingAgent? templateRouter = null,
    FixedReplyTemplateService? fixedReplies = null,
    IMessageIntentAgent? intentAgent = null,
    IMessageIntentAuditStore? intentAudits = null,
    AgentRuntimeOptions? runtimeOptions = null,
    IAnswerAgent? answerAgent = null)
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
        var policy = await conversations.EvaluateInboundPolicyAsync(
            payload.MessageId,
            payload.GroupName,
            payload.GroupRemark,
            payload.WasMentioned,
            cancellationToken);
        if (policy.Kind == InboundPolicyDecisionKind.NoReply)
        {
            await conversations.PersistNoReplyTerminalAsync(policy, cancellationToken);
            return;
        }
        var runtime = runtimeOptions ?? new AgentRuntimeOptions();
        runtime.Validate();
        if (runtime.IntentRuntimeMode == IntentRuntimeMode.Paused)
        {
            var paused = new MessageIntentResult(
                IntentDecision.NoReply,
                IntentCategory.Uncertain,
                "insufficient_context",
                1,
                "intent_runtime_paused");
            if (intentAudits is not null && policy.GroupProfileId is { } pausedGroupId)
            {
                await intentAudits.RecordAsync(
                    new(
                        payload.MessageId,
                        pausedGroupId,
                        IntentRuntimeMode.Paused,
                        paused,
                        false,
                        timeProvider.GetUtcNow().UtcDateTime),
                    cancellationToken);
            }
            await conversations.PersistNoReplyTerminalAsync(
                new(
                    payload.MessageId,
                    InboundPolicyDecisionKind.NoReply,
                    policy.GroupProfileId,
                    "intent_runtime_paused",
                    """{"decision":"no_reply","reason":"intent_runtime_paused"}"""),
                cancellationToken);
            return;
        }
        var request = await conversations.LeaseForProcessingAsync(payload.MessageId, sessionLeaseOwner, timeProvider.GetUtcNow().UtcDateTime,
            SessionLeaseDuration, cancellationToken);
        var committed = false;
        try
        {
        if (runtime.IntentRuntimeMode is IntentRuntimeMode.Shadow or IntentRuntimeMode.AgentFramework)
        {
            var groupId = policy.GroupProfileId
                ?? throw new InvalidOperationException("Proceed policy must identify one group.");
            var intent = intentAgent is null
                ? new MessageIntentResult(
                    IntentDecision.Uncertain,
                    IntentCategory.Uncertain,
                    "insufficient_context",
                    0,
                    "intent_agent_unavailable")
                : await intentAgent.DecideAsync(
                    new(payload.MessageId, groupId, payload.WasMentioned),
                    cancellationToken);
            var formalConversationIncluded =
                runtime.IntentRuntimeMode == IntentRuntimeMode.Shadow
                || intent.Decision == IntentDecision.Reply;
            if (intentAudits is not null)
            {
                await intentAudits.RecordAsync(
                    new(
                        payload.MessageId,
                        groupId,
                        runtime.IntentRuntimeMode,
                        intent,
                        formalConversationIncluded,
                        timeProvider.GetUtcNow().UtcDateTime),
                    cancellationToken);
            }
            if (runtime.IntentRuntimeMode == IntentRuntimeMode.AgentFramework
                && intent.Decision != IntentDecision.Reply)
            {
                var reason = intent.FailureCode
                    ?? (intent.Decision == IntentDecision.NoReply
                        ? intent.ReasonCode
                        : "intent_agent_uncertain");
                await conversations.PersistNoReplyTerminalAsync(
                    new(
                        payload.MessageId,
                        InboundPolicyDecisionKind.NoReply,
                        groupId,
                        reason,
                        JsonSerializer.Serialize(new
                        {
                            decision = "no_reply",
                            reason,
                            category = intent.Category.ToString(),
                            runtimeMode = runtime.IntentRuntimeMode.ToString()
                        })),
                    cancellationToken);
                return;
            }
        }
        if (ConversationalGreeting.TryCreate(request.Question, out var greeting))
        {
            await EnsureLeaseAsync(request, cancellationToken);
            await conversations.PersistAnswerAndEnqueueAsync(
                request,
                greeting,
                cancellationToken);
            committed = true;
            return;
        }
        if (runtime.TemplateRoutingRuntimeMode == TemplateRoutingRuntimeMode.AgentFramework
            && templateRouter is not null
            && fixedReplies is not null)
        {
            var route = await templateRouter.RouteAsync(
                request.GroupProfileId,
                request.Question,
                cancellationToken);
            if (route is MatchFixedTemplate match)
            {
                var fixedReply = await fixedReplies.ResolveAsync(
                    match.TemplateId,
                    match.ExpectedVersion,
                    request.GroupProfileId,
                    cancellationToken);
                if (fixedReply is not null)
                {
                    await EnsureLeaseAsync(request, cancellationToken);
                    var fixedResult = new GroundedAnswerResult(
                        new AnswerDecision(
                            AnswerDecisionKind.Answer,
                            fixedReply.ReplyText),
                        new RetrievalAuditDraft(
                            [],
                            0,
                            1,
                            "fixed_template",
                            "answer",
                            AnswerSource: "fixed_template",
                            FixedReplyTemplateId: fixedReply.Id,
                            FixedReplyTemplateVersion: fixedReply.Version));
                    await conversations.PersistAnswerAndEnqueueAsync(
                        request,
                        fixedResult,
                        cancellationToken);
                    committed = true;
                    return;
                }
            }
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
        await EnsureLeaseAsync(request, cancellationToken);
        var retrievalPreparation = await multiTurnRetrieval.PrepareAsync(
            new QueryRewriteRequest(
                request.MessageId,
                request.ConversationSessionId,
                ConversationChannelType.Group,
                request.GroupProfileId,
                request.RobotConfigId,
                request.Scope.ScopeKey,
                request.SenderDisplayName,
                request.Question,
                effectiveContext,
                request.ChatConfiguration,
                request.ModelConfigurationId),
            cancellationToken);
        await EnsureLeaseAsync(request, cancellationToken);
        if (retrievalPreparation.TerminalAnswer is not null)
        {
            var terminalResult = multiTurnRetrieval.CreateTerminalResult(
                retrievalPreparation,
                request.ContextPolicy);
            if (effectiveContext.WasIdleReset)
            {
                terminalResult = terminalResult with
                {
                    ResetContextBeforeCurrent = true,
                    UpdatedSummary = null
                };
            }
            if (updatedSummary is not null)
            {
                terminalResult = terminalResult with
                {
                    UpdatedSummary = updatedSummary
                };
            }
            await conversations.PersistAnswerAndEnqueueAsync(
                request,
                terminalResult,
                cancellationToken);
            committed = true;
            return;
        }

        var retrievalQuery = retrievalPreparation.RetrievalQuery
            ?? throw new InvalidOperationException(
                "Query rewrite produced neither retrieval nor terminal output.");
        var answerRequest = new GroundedAnswerRequest(request.MessageId, request.GroupProfileId, request.Scope.ScopeKey, request.Question,
            request.AllowedTagIds, effectiveContext, request.ContextPolicy, request.ChatConfiguration, retrievalQuery, request.ModelConfigurationId,
            request.Scope.DegradationReason, summaryFailureCode, request.AnswerFallback,
            RobotConfigId: request.RobotConfigId,
            SubjectKey: request.SenderDisplayName,
            SenderDisplayName: request.SenderDisplayName,
            QueryRewriteAudit: retrievalPreparation.Audit);
        var result = runtime.AnswerRuntimeMode == AnswerRuntimeMode.AgentFramework
                     && answerAgent is not null
            ? await answerAgent.AnswerAsync(answerRequest, cancellationToken)
            : await answers.AnswerAsync(answerRequest, cancellationToken);
        if (effectiveContext.WasIdleReset) result = result with { ResetContextBeforeCurrent = true, UpdatedSummary = null };
        if (updatedSummary is not null) result = result with { UpdatedSummary = updatedSummary };
        await EnsureLeaseAsync(request, cancellationToken);
        await conversations.PersistAnswerAndEnqueueAsync(request, result, cancellationToken);
        committed = true;
        }
        finally
        {
            if (!committed) await conversations.ReleaseLeaseAsync(request.ConversationSessionId, sessionLeaseOwner, CancellationToken.None);
        }
    }

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
        public string? GroupRemark { get; init; }
        public bool WasMentioned { get; init; }
    }
}
