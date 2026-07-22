using WechatRobot.Application.Groups;

namespace WechatRobot.Application.Conversations;

public sealed record ConversationHistoryMessage(string Role, string SenderExternalUserId, string Content, DateTime CreatedAtUtc);
public sealed record ConversationContextResult(IReadOnlyList<ConversationHistoryMessage> Messages, string? Summary, bool WasIdleReset, bool WasTokenLimited);

public sealed class ConversationContextService
{
    public ConversationContextResult Build(
        IEnumerable<ConversationHistoryMessage> history,
        GroupContextSettings policy,
        string senderExternalUserId,
        DateTime nowUtc,
        string? summary = null)
    {
        var filtered = history
            .Where(message => !policy.SenderIsolated || string.Equals(message.SenderExternalUserId, senderExternalUserId, StringComparison.Ordinal))
            .Where(message => policy.IncludeBotHistory || !string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            .OrderBy(message => message.CreatedAtUtc)
            .ToArray();

        if (filtered.Length > 0 && filtered[^1].CreatedAtUtc < nowUtc.AddMinutes(-policy.IdleTimeoutMinutes))
        {
            return new([], null, true, false);
        }

        var maximumMessages = Math.Max(0, policy.HistoryTurns * 2);
        var selected = maximumMessages == 0 ? [] : filtered.TakeLast(maximumMessages).ToList();
        var wasTokenLimited = false;
        var tokenCount = selected.Sum(message => EstimateTokens(message.Content));
        while (selected.Count > 1 && tokenCount > policy.TokenCap)
        {
            tokenCount -= EstimateTokens(selected[0].Content);
            selected.RemoveAt(0);
            wasTokenLimited = true;
        }

        return new(selected, policy.SummaryEnabled && !string.IsNullOrWhiteSpace(summary) ? summary : null, false, wasTokenLimited);
    }

    private static int EstimateTokens(string content) => Math.Max(1, (content.Length + 3) / 4);
}
