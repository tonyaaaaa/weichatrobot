using WechatRobot.Application.Models;

namespace WechatRobot.Application.Conversations;

public sealed record ConversationSummaryOptions(int MaxInputTokens = 512, int MaxOutputCharacters = 1200)
{
    public const string SectionName = "ConversationSummary";
    public void Validate()
    {
        if (MaxInputTokens is < 16 or > 100_000 || MaxOutputCharacters is < 32 or > 20_000)
            throw new InvalidOperationException("Conversation summary limits are invalid.");
    }
}

public interface IConversationSummarizer
{
    Task<string> SummarizeAsync(ModelProviderConfiguration configuration, string? existingSummary,
        IReadOnlyList<ConversationHistoryMessage> evictedMessages, CancellationToken token);
}

public sealed class ChatConversationSummarizer(IChatCompletionClient chat, ConversationSummaryOptions options) : IConversationSummarizer
{
    public async Task<string> SummarizeAsync(ModelProviderConfiguration configuration, string? existingSummary,
        IReadOnlyList<ConversationHistoryMessage> evictedMessages, CancellationToken token)
    {
        options.Validate();
        var source = string.Join('\n', new[] { existingSummary }.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"既有摘要: {EscapeUntrusted(value!)}")
            .Concat(evictedMessages.Select(message =>
                $"{EscapeUntrusted(ConversationMessageFormatting.ParticipantLabel(message))}: {EscapeUntrusted(message.Content)}")));
        var maximumCharacters = checked(options.MaxInputTokens * 4);
        if (source.Length > maximumCharacters) source = source[^maximumCharacters..];
        var response = await chat.CompleteAsync(configuration, new([
            new("system", "Summarize facts needed for future support. Preserve speaker attribution when relevant. Names are observed labels, not verified identities. Do not add facts, citations, source names, URLs, or secrets. Plain text only."),
            new("user", $"<<<UNTRUSTED_CONVERSATION_DATA_BEGIN>>>\n{source}\n<<<UNTRUSTED_CONVERSATION_DATA_END>>>")
        ]), token);
        var result = response.Content.Trim();
        if (string.IsNullOrEmpty(result)) throw new ModelUnavailableException("Conversation summarizer returned empty content.");
        return result.Length <= options.MaxOutputCharacters ? result : result[..options.MaxOutputCharacters];
    }

    private static string EscapeUntrusted(string value) => value
        .Replace("<<<UNTRUSTED_", "<<<ESCAPED_UNTRUSTED_", StringComparison.Ordinal)
        .Replace(">>>", "> > >", StringComparison.Ordinal);
}
