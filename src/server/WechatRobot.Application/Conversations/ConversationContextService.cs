using WechatRobot.Application.Groups;

namespace WechatRobot.Application.Conversations;

public sealed record ConversationHistoryMessage(string Role, string SessionScopeKey, string Content, DateTime CreatedAtUtc, Guid? MessageId = null,
    long? SessionSequence = null);
public sealed record ConversationContextResult
{
    public ConversationContextResult(IReadOnlyList<ConversationHistoryMessage> messages, string? summary, bool wasIdleReset, bool wasTokenLimited,
        IReadOnlyList<ConversationHistoryMessage>? evictedMessages = null, int contextTokenCount = 0)
    {
        Messages = messages;
        Summary = summary;
        WasIdleReset = wasIdleReset;
        WasTokenLimited = wasTokenLimited;
        EvictedMessages = evictedMessages ?? [];
        ContextTokenCount = contextTokenCount;
    }

    public IReadOnlyList<ConversationHistoryMessage> Messages { get; }
    public string? Summary { get; }
    public bool WasIdleReset { get; }
    public bool WasTokenLimited { get; }
    public IReadOnlyList<ConversationHistoryMessage> EvictedMessages { get; }
    public int ContextTokenCount { get; }
}

public sealed class ConversationContextService
{
    public ConversationContextResult Build(
        IEnumerable<ConversationHistoryMessage> history,
        GroupContextSettings policy,
        string sessionScopeKey,
        DateTime nowUtc,
        string? summary = null)
    {
        var filtered = history
            .Where(message => !policy.SenderIsolated || string.Equals(message.SessionScopeKey, sessionScopeKey, StringComparison.Ordinal))
            .Where(message => policy.IncludeBotHistory || !string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            .OrderBy(message => message.SessionSequence ?? 0)
            .ThenBy(message => message.MessageId ?? Guid.Empty)
            .ToArray();

        if (filtered.Length > 0 && filtered[^1].CreatedAtUtc < nowUtc.AddMinutes(-policy.IdleTimeoutMinutes))
        {
            return new([], null, true, false, [], 0);
        }

        var selected = SelectTurns(filtered, policy).ToList();
        var wasTokenLimited = false;
        var tokenCount = ContextWrapperBaseTokens(selected.Count > 0) + selected.Sum(MessageTokens);
        while (selected.Count > 0 && tokenCount > policy.TokenCap)
        {
            selected.RemoveAt(0);
            wasTokenLimited = true;
            tokenCount = ContextWrapperBaseTokens(selected.Count > 0) + selected.Sum(MessageTokens);
        }

        string? boundedSummary = null;
        var requestedSummary = policy.SummaryEnabled && !string.IsNullOrWhiteSpace(summary) ? summary.Trim() : null;
        if (requestedSummary is not null)
        {
            var baseTokens = ContextWrapperBaseTokens(true) + selected.Sum(MessageTokens);
            var available = Math.Max(0, policy.TokenCap - baseTokens - SummaryLabelTokens);
            boundedSummary = TruncateToTokens(requestedSummary, available);
            if (!string.Equals(boundedSummary, requestedSummary, StringComparison.Ordinal)) wasTokenLimited = true;
            tokenCount = baseTokens + (boundedSummary is null ? 0 : SummaryLabelTokens + EstimateTokens(boundedSummary));
        }
        else tokenCount = ContextWrapperBaseTokens(selected.Count > 0) + selected.Sum(MessageTokens);

        var selectedIds = selected.Select(message => message.MessageId).ToHashSet();
        var evicted = filtered.Where(message => message.MessageId is null
            ? !selected.Contains(message)
            : !selectedIds.Contains(message.MessageId)).ToArray();
        return new(selected, boundedSummary, false, wasTokenLimited, evicted, Math.Min(tokenCount, policy.TokenCap));
    }

    private static IReadOnlyList<ConversationHistoryMessage> SelectTurns(IReadOnlyList<ConversationHistoryMessage> filtered, GroupContextSettings policy)
    {
        if (policy.HistoryTurns <= 0) return [];
        if (!policy.IncludeBotHistory)
            return filtered.Where(message => string.Equals(message.Role, "user", StringComparison.Ordinal)).TakeLast(policy.HistoryTurns).ToArray();
        var userIndexes = filtered.Select((message, index) => (message, index)).Where(item => item.message.Role == "user").Select(item => item.index).ToArray();
        if (userIndexes.Length == 0) return [];
        var start = userIndexes[Math.Max(0, userIndexes.Length - policy.HistoryTurns)];
        return filtered.Skip(start).ToArray();
    }

    private const int SummaryLabelTokens = 3;
    private static int ContextWrapperBaseTokens(bool hasContent) => hasContent ? 4 : 0;
    private static int MessageTokens(ConversationHistoryMessage message) => 3 + EstimateTokens(message.Content);
    private static int EstimateTokens(string content) => Math.Max(1, (content.Length + 3) / 4);
    private static string? TruncateToTokens(string content, int maximumTokens)
    {
        if (maximumTokens <= 0) return null;
        var maximumCharacters = maximumTokens * 4;
        return content.Length <= maximumCharacters ? content : content[..maximumCharacters];
    }
}
