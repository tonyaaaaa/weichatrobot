using WechatRobot.Application.Groups;

namespace WechatRobot.Application.Conversations;

public sealed record ConversationHistoryMessage(string Role, string SessionScopeKey, string Content, DateTime CreatedAtUtc, Guid? MessageId = null);
public sealed record ConversationContextResult
{
    public ConversationContextResult(IReadOnlyList<ConversationHistoryMessage> messages, string? summary, bool wasIdleReset, bool wasTokenLimited,
        IReadOnlyList<ConversationHistoryMessage>? evictedMessages = null)
    {
        Messages = messages;
        Summary = summary;
        WasIdleReset = wasIdleReset;
        WasTokenLimited = wasTokenLimited;
        EvictedMessages = evictedMessages ?? [];
    }

    public IReadOnlyList<ConversationHistoryMessage> Messages { get; }
    public string? Summary { get; }
    public bool WasIdleReset { get; }
    public bool WasTokenLimited { get; }
    public IReadOnlyList<ConversationHistoryMessage> EvictedMessages { get; }
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
            .OrderBy(message => message.CreatedAtUtc)
            .ToArray();

        if (filtered.Length > 0 && filtered[^1].CreatedAtUtc < nowUtc.AddMinutes(-policy.IdleTimeoutMinutes))
        {
            return new([], null, true, false, filtered);
        }

        var selected = SelectTurns(filtered, policy).ToList();
        var wasTokenLimited = false;
        var tokenCount = selected.Sum(message => EstimateTokens(message.Content));
        while (selected.Count > 0 && tokenCount > policy.TokenCap)
        {
            tokenCount -= EstimateTokens(selected[0].Content);
            selected.RemoveAt(0);
            wasTokenLimited = true;
        }

        var selectedIds = selected.Select(message => message.MessageId).ToHashSet();
        var evicted = filtered.Where(message => message.MessageId is null
            ? !selected.Contains(message)
            : !selectedIds.Contains(message.MessageId)).ToArray();
        return new(selected, policy.SummaryEnabled && !string.IsNullOrWhiteSpace(summary) ? summary : null, false, wasTokenLimited, evicted);
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

    private static int EstimateTokens(string content) => Math.Max(1, (content.Length + 3) / 4);
}
