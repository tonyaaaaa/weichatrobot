namespace WechatRobot.Application.Conversations;

public static class ConversationalGreeting
{
    private const string ReplyText = "您好！请问有什么签证问题需要咨询？";

    private static readonly HashSet<string> Greetings = new(StringComparer.OrdinalIgnoreCase)
    {
        "你好",
        "您好",
        "嗨",
        "哈喽",
        "hello",
        "hi",
        "在吗"
    };

    public static bool TryCreate(string input, out GroundedAnswerResult result)
    {
        var normalized = input.Trim().TrimEnd('。', '！', '？', '!', '?').Trim();
        if (!Greetings.Contains(normalized))
        {
            result = null!;
            return false;
        }

        result = new(
            new AnswerDecision(AnswerDecisionKind.Answer, ReplyText),
            new RetrievalAuditDraft(
                [],
                0,
                1,
                "conversational_greeting",
                "answer",
                AnswerSource: "conversational_greeting",
                WebSearchSources: []));
        return true;
    }
}
