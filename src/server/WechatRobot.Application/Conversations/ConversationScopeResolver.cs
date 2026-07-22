using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WechatRobot.Application.Conversations;

public sealed record ConversationScope(string ScopeKey, bool IsStatelessDegradation, string? DegradationReason);

public static partial class ConversationScopeResolver
{
    public static ConversationScope Resolve(bool senderIsolated, string? stableSenderId, Guid messageId)
    {
        if (!senderIsolated) return new("group", false, null);
        var normalized = stableSenderId?.Trim();
        if (normalized is null || !StableIdPattern().IsMatch(normalized))
            return new($"stateless:{messageId:N}", true, "stable_sender_id_unavailable");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return new($"sender:{hash}", false, null);
    }

    [GeneratedRegex("^[A-Za-z0-9_.:@-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdPattern();
}
