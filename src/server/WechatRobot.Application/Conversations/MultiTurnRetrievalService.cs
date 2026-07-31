using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Groups;

namespace WechatRobot.Application.Conversations;

public sealed record QueryRewriteAudit(
    QueryRewriteDecision Decision,
    QueryRewriteReasonCode ReasonCode,
    Guid ConversationSessionId,
    ConversationChannelType ChannelType,
    Guid ModelConfigurationId,
    IReadOnlyList<Guid> ContextMessageIds,
    int DurationMilliseconds,
    bool UsedOriginalQuestion,
    bool RagExecuted,
    string OriginalQuestionHash,
    int OriginalQuestionLength,
    string? StandaloneQueryHash,
    int? StandaloneQueryLength,
    string? FailureCode)
{
    public QueryRewriteAuditSummary ToSummary() =>
        new(
            Decision.ToString(),
            ReasonCode switch
            {
                QueryRewriteReasonCode.StandaloneQuestion =>
                    "standalone_question",
                QueryRewriteReasonCode.ContextualFollowUp =>
                    "contextual_follow_up",
                QueryRewriteReasonCode.AmbiguousReference =>
                    "ambiguous_reference",
                QueryRewriteReasonCode.ConflictingContext =>
                    "conflicting_context",
                QueryRewriteReasonCode.InvalidOutput =>
                    "invalid_output",
                QueryRewriteReasonCode.ProviderTimeout =>
                    "provider_timeout",
                QueryRewriteReasonCode.ProviderFailure =>
                    "provider_failure",
                _ => "invalid_output"
            },
            ConversationSessionId,
            ChannelType.ToString(),
            ModelConfigurationId,
            ContextMessageIds,
            DurationMilliseconds,
            UsedOriginalQuestion,
            RagExecuted,
            OriginalQuestionHash,
            OriginalQuestionLength,
            StandaloneQueryHash,
            StandaloneQueryLength,
            FailureCode);
}

public sealed record QueryRewriteAuditSummary(
    string RewriteDecision,
    string RewriteReasonCode,
    Guid ConversationSessionId,
    string ChannelType,
    Guid ModelConfigurationId,
    IReadOnlyList<Guid> ContextMessageIds,
    int DurationMilliseconds,
    bool UsedOriginalQuestion,
    bool RagExecuted,
    string OriginalQuestionHash,
    int OriginalQuestionLength,
    string? StandaloneQueryHash,
    int? StandaloneQueryLength,
    string? FailureCode);

public sealed record MultiTurnRetrievalPreparation(
    RetrievalQueryResult? RetrievalQuery,
    AnswerDecision? TerminalAnswer,
    QueryRewriteAudit Audit);

public sealed class MultiTurnRetrievalService(
    IQueryRewriteAgent agent,
    RetrievalQueryOptions retrievalOptions,
    AnswerOutputFirewall outputFirewall,
    GroundedAnswerOptions answerOptions)
{
    public const string SafeClarificationText =
        "请明确您咨询的具体对象或类型，我会重新核对。";

    public async Task<MultiTurnRetrievalPreparation> PrepareAsync(
        QueryRewriteRequest request,
        CancellationToken cancellationToken)
    {
        retrievalOptions.Validate();
        answerOptions.Validate();
        var result = await agent.RewriteAsync(request, cancellationToken);
        var contextMessageIds = request.Context.Messages
            .Where(message => message.MessageId.HasValue)
            .Select(message => message.MessageId!.Value)
            .ToArray();
        var hasFormalContext =
            contextMessageIds.Length > 0
            || !string.IsNullOrWhiteSpace(request.Context.Summary);

        return result.Decision switch
        {
            QueryRewriteDecision.Search =>
                PrepareSearch(request, result, contextMessageIds),
            QueryRewriteDecision.Clarification =>
                PrepareClarification(request, result, contextMessageIds),
            QueryRewriteDecision.Failure =>
                PrepareFailure(request, result, contextMessageIds, hasFormalContext),
            _ => PrepareInvalid(request, contextMessageIds)
        };
    }

    public GroundedAnswerResult CreateTerminalResult(
        MultiTurnRetrievalPreparation preparation,
        GroupContextSettings contextPolicy)
    {
        if (preparation.TerminalAnswer is null)
        {
            throw new InvalidOperationException(
                "A retrieval preparation without a terminal answer cannot be persisted directly.");
        }

        var policy =
            $"senderIsolated={contextPolicy.SenderIsolated};turns={contextPolicy.HistoryTurns};idleMinutes={contextPolicy.IdleTimeoutMinutes};tokenCap={contextPolicy.TokenCap};summary={contextPolicy.SummaryEnabled};botHistory={contextPolicy.IncludeBotHistory}";
        return new GroundedAnswerResult(
            preparation.TerminalAnswer,
            new RetrievalAuditDraft(
                [],
                answerOptions.ConfidenceThreshold,
                null,
                policy,
                preparation.TerminalAnswer.Kind.ToString(),
                preparation.Audit.FailureCode,
                JsonSerializer.Serialize(new
                {
                    QueryRewrite = preparation.Audit.ToSummary()
                }),
                "none"));
    }

    private MultiTurnRetrievalPreparation PrepareSearch(
        QueryRewriteRequest request,
        QueryRewriteResult result,
        IReadOnlyList<Guid> contextMessageIds)
    {
        var query = result.StandaloneQuery?.Trim();
        var maximumCharacters = checked(retrievalOptions.TokenCap * 4);
        if (string.IsNullOrWhiteSpace(query)
            || query.Length > maximumCharacters
            || !string.IsNullOrWhiteSpace(result.ClarificationQuestion)
            || !IsPlainText(query))
        {
            return PrepareInvalid(request, contextMessageIds);
        }

        var usedOriginal = string.Equals(
            query,
            request.CurrentQuestion.Trim(),
            StringComparison.Ordinal);
        return new(
            new RetrievalQueryResult(query, contextMessageIds),
            null,
            Audit(
                request,
                result.Decision,
                result.ReasonCode,
                contextMessageIds,
                result.DurationMilliseconds,
                usedOriginal,
                true,
                query,
                result.FailureCode));
    }

    private MultiTurnRetrievalPreparation PrepareClarification(
        QueryRewriteRequest request,
        QueryRewriteResult result,
        IReadOnlyList<Guid> contextMessageIds)
    {
        var clarification = result.ClarificationQuestion?.Trim();
        var failureCode = result.FailureCode;
        if (string.IsNullOrWhiteSpace(clarification)
            || clarification.Length > 500
            || !string.IsNullOrWhiteSpace(result.StandaloneQuery)
            || !outputFirewall.ValidateUngrounded(clarification).IsSafe)
        {
            clarification = SafeClarificationText;
            failureCode = "unsafe_clarification";
        }

        return new(
            null,
            new AnswerDecision(
                AnswerDecisionKind.Clarification,
                clarification),
            Audit(
                request,
                result.Decision,
                result.ReasonCode,
                contextMessageIds,
                result.DurationMilliseconds,
                false,
                false,
                null,
                failureCode));
    }

    private MultiTurnRetrievalPreparation PrepareFailure(
        QueryRewriteRequest request,
        QueryRewriteResult result,
        IReadOnlyList<Guid> contextMessageIds,
        bool hasFormalContext)
    {
        if (!string.IsNullOrWhiteSpace(result.StandaloneQuery)
            || !string.IsNullOrWhiteSpace(result.ClarificationQuestion))
        {
            return PrepareInvalid(request, contextMessageIds);
        }
        if (result.ReasonCode == QueryRewriteReasonCode.InvalidOutput)
        {
            return PrepareInvalid(request, contextMessageIds);
        }

        if (!hasFormalContext)
        {
            var query = BoundOriginalQuestion(request.CurrentQuestion);
            return new(
                new RetrievalQueryResult(query, []),
                null,
                Audit(
                    request,
                    result.Decision,
                    result.ReasonCode,
                    [],
                    result.DurationMilliseconds,
                    true,
                    true,
                    query,
                    result.FailureCode));
        }

        return new(
            null,
            new AnswerDecision(
                AnswerDecisionKind.SystemFailure,
                answerOptions.SystemFailureText),
            Audit(
                request,
                result.Decision,
                result.ReasonCode,
                contextMessageIds,
                result.DurationMilliseconds,
                false,
                false,
                null,
                result.FailureCode));
    }

    private MultiTurnRetrievalPreparation PrepareInvalid(
        QueryRewriteRequest request,
        IReadOnlyList<Guid> contextMessageIds) =>
        new(
            null,
            new AnswerDecision(
                AnswerDecisionKind.SystemFailure,
                answerOptions.SystemFailureText),
            Audit(
                request,
                QueryRewriteDecision.Failure,
                QueryRewriteReasonCode.InvalidOutput,
                contextMessageIds,
                0,
                false,
                false,
                null,
                "query_rewrite_invalid_output"));

    private string BoundOriginalQuestion(string question)
    {
        var value = question.Trim();
        var maximumCharacters = checked(retrievalOptions.TokenCap * 4);
        return value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];
    }

    private static QueryRewriteAudit Audit(
        QueryRewriteRequest request,
        QueryRewriteDecision decision,
        QueryRewriteReasonCode reasonCode,
        IReadOnlyList<Guid> contextMessageIds,
        int durationMilliseconds,
        bool usedOriginalQuestion,
        bool ragExecuted,
        string? standaloneQuery,
        string? failureCode) =>
        new(
            decision,
            reasonCode,
            request.ConversationSessionId,
            request.ChannelType,
            request.ModelConfigurationId,
            contextMessageIds,
            Math.Max(0, durationMilliseconds),
            usedOriginalQuestion,
            ragExecuted,
            Hash(request.CurrentQuestion),
            request.CurrentQuestion.Length,
            standaloneQuery is null ? null : Hash(standaloneQuery),
            standaloneQuery?.Length,
            failureCode);

    private static bool IsPlainText(string value) =>
        !value.Any(character =>
            char.IsControl(character)
            && character is not ('\r' or '\n' or '\t'))
        && !value.Contains("<<<UNTRUSTED_", StringComparison.Ordinal)
        && !value.Contains("\"tool_calls\"", StringComparison.OrdinalIgnoreCase);

    private static string Hash(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
