namespace WechatRobot.Application.Conversations;

public sealed record RetrievalQueryOptions(int TokenCap = 512)
{
    public const string SectionName = "RetrievalQuery";
    public void Validate() { if (TokenCap is < 8 or > 100_000) throw new InvalidOperationException("Retrieval query token cap is invalid."); }
}

public sealed record RetrievalQueryResult(string Query, IReadOnlyList<Guid> ContextMessageIds);

public sealed class RetrievalQueryBuilder(RetrievalQueryOptions options)
{
    public RetrievalQueryResult Build(string currentQuestion, ConversationContextResult context)
    {
        options.Validate();
        var maximumCharacters = checked(options.TokenCap * 4);
        var question = currentQuestion.Trim();
        if (question.Length > maximumCharacters) question = question[..maximumCharacters];
        var parts = new List<string>();
        var ids = new List<Guid>();
        var used = question.Length;
        foreach (var message in context.Messages.Reverse())
        {
            var part = $"{message.Role}: {message.Content}";
            if (used + part.Length + 1 > maximumCharacters) continue;
            parts.Insert(0, part);
            used += part.Length + 1;
            if (message.MessageId is { } id) ids.Insert(0, id);
        }
        if (!string.IsNullOrWhiteSpace(context.Summary))
        {
            var part = $"summary: {context.Summary}";
            if (used + part.Length + 1 <= maximumCharacters) parts.Insert(0, part);
        }
        parts.Add(question);
        return new(string.Join('\n', parts), ids);
    }
}
