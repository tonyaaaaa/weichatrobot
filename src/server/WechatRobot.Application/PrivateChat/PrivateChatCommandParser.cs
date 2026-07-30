namespace WechatRobot.Application.PrivateChat;

public enum PrivateChatMessageKind
{
    Question,
    DirectKnowledgeIngest,
    UnsupportedIngest
}

public sealed record PrivateChatCommand(
    PrivateChatMessageKind Kind,
    string Body);

public static class PrivateChatCommandParser
{
    private const string IngestMarker = "#知识入库";

    public static PrivateChatCommand Parse(int roomType, string text)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var hasMarker = normalized.StartsWith(IngestMarker, StringComparison.Ordinal);
        var hasSeparator = normalized.Length > IngestMarker.Length
                           && char.IsWhiteSpace(normalized[IngestMarker.Length]);
        var body = hasMarker && hasSeparator
            ? normalized[IngestMarker.Length..].Trim()
            : string.Empty;
        if (!hasMarker
            || !hasSeparator
            || body.Length == 0)
        {
            return new PrivateChatCommand(
                PrivateChatMessageKind.Question,
                normalized.Trim());
        }
        return roomType == 4
            ? new PrivateChatCommand(
                PrivateChatMessageKind.DirectKnowledgeIngest,
                body)
            : new PrivateChatCommand(
                PrivateChatMessageKind.UnsupportedIngest,
                body);
    }
}
